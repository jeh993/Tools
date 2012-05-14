﻿/*
* Copyright (c) 2012 Robert Nagy and Thomas III Kaltz
*
* This file is part of CasparRx Framework.
*
* CasparRx is free software: you can redistribute it and/or modify
* it under the terms of the GNU General Public License as published by
* the Free Software Foundation, either version 3 of the License, or
* (at your option) any later version.
*
* CasparRx is distributed in the hope that it will be useful,
* but WITHOUT ANY WARRANTY; without even the implied warranty of
* MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
* GNU General Public License for more details.
*
* You should have received a copy of the GNU General Public License
* along with CasparRX. If not, see <http://www.gnu.org/licenses/>.
*
*/

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Reactive;
using System.Reactive.Linq;
using System.Reactive.Disposables;
using System.Reactive.Concurrency;
using System.IO;
using System.Net.Sockets;
using System.Reactive.Subjects;
using System.Net;
using System.Text.RegularExpressions;
using System.Threading;
using System.Diagnostics;

namespace CasparRx
{
    public class Connection : IDisposable
    {
        private string host;
        private int port;

        private BehaviorSubject<bool>       connectedSubject = new BehaviorSubject<bool>(false);
        private BehaviorSubject<Version>    versionSubject = new BehaviorSubject<Version>(null);
        private volatile TcpClient          client = null;
        private EventLoopScheduler          scheduler = new EventLoopScheduler(ts => new Thread(ts));
        private IDisposable                 reconnectSubscription = null;

        public class Version
        {
            public Version(int gen, int maj, int min, int rev, String tag)
            {
                this.Generation = gen;
                this.Major = maj;
                this.Minor = min;
                this.Revision = rev;
                this.Tag = tag;
            }

            public int Generation { get; private set; }
            public int Major { get; private set; }
            public int Minor { get; private set; }
            public int Revision { get; private set; }
            public String Tag { get; private set; }

            public override string ToString()
            {
                return this.Generation.ToString() + "." +
                       this.Major.ToString()      + "." +
                       this.Minor.ToString()      + "." +
                       this.Revision.ToString()   + " " +
                       this.Tag.ToString();
            }
        };

        public IObservable<bool> OnConnected
        {
            get { return this.connectedSubject
                             .DistinctUntilChanged(); }
        }
        
        public IObservable<Version> OnVersion
        {
            get
            {
                return this.AsyncSend("VERSION")
                            .Select(x =>
                            {
                                var exp = new Regex(@"(\d+)\.(\d+)\.(\d+)\.(\d+)\s+(.*)");

                                var match = exp.Match(x);
                                if (!match.Success)
                                    throw new Exception("Invalid VERSION response");

                                return new Version(int.Parse(match.Groups[1].Value),
                                                   int.Parse(match.Groups[2].Value),
                                                   int.Parse(match.Groups[3].Value),
                                                   int.Parse(match.Groups[4].Value),
                                                   match.Groups[5].Value);
                            });
            }
        }

        public Connection(string host, int port = 5250)
        {
            this.host = host;
            this.port = port;
            
            this.Connect();
            
            this.reconnectSubscription = Observable
                .Interval(TimeSpan.FromSeconds(1))
                .ObserveOn(scheduler)
                .Subscribe(x => this.Connect());
        }

        public void Dispose()
        {
            if (this.reconnectSubscription != null)
                this.reconnectSubscription.Dispose();
            this.reconnectSubscription = null;

            Observable
                .Start(() => this.Reset(), this.scheduler)
                .ObserveOn(Scheduler.ThreadPool)
                .Subscribe(x => this.scheduler.Dispose());
        }

        public IEnumerable<string> Send(string cmd)
        {
            var result = this.DoAsyncSend(cmd);

            // Block only until we get response code and know that the command has executed.
            result.First();

            return result
                .Skip(1) // Skip response code.
                .ToEnumerable();
        }

        public IObservable<string> AsyncSend(string cmd)
        {
            return this
                .DoAsyncSend(cmd)
                .Skip(1);  // Skip response code.
        }

        private void Reset()
        {
            if (this.client != null)
                client.Close();
            this.client = null;
            this.connectedSubject.OnNext(false);
        }

        private bool Connect()
        {
            try
            {
                if (this.IsConnected)
                    return true;

                this.Reset();
                this.client = new TcpClient(this.host, this.port) { ReceiveTimeout = 5000 };               
            }
            catch
            {
                this.Reset();
            }

            return this.IsConnected;
        }

        private bool IsConnected
        {
            get
            {
                if (this.client == null || this.client.Client == null || !this.client.Connected)
                    return false;

                bool blockingState = this.client.Client.Blocking;
                try
                {
                    this.client.Client.Blocking = false;
                    this.client.Client.Send(new byte[1], 0, 0);
                    this.connectedSubject.OnNext(true);
                    return true;
                }
                catch (SocketException e)
                {
                    // 10035 == WSAEWOULDBLOCK
                    if (e.NativeErrorCode.Equals(10035))
                    {
                        this.connectedSubject.OnNext(true);
                        return true;
                    }
                    else
                    {
                        this.Reset();
                        this.connectedSubject.OnNext(false);
                        return false;
                    }
                }
                finally
                {
                    if(this.client != null && this.client.Client != null)
                        this.client.Client.Blocking = blockingState;
                }           
            }
        }

        private string ReadLine(StreamReader reader)
        {
            var str = new StringBuilder();
            while (true)
            {
                var c = reader.Read();
                if (c == -1)
                    throw new IOException();

                str.Append((char)c);

                if (str.Length >= 2 && str[str.Length - 2] == '\r' && str[str.Length - 1] == '\n')
                    break;
            }

            return str.ToString(0, str.Length - 2);
        }

        private IObservable<string> DoAsyncSend(string cmd)
        { 
            var subject = new ReplaySubject<string>();

            this.scheduler.Schedule(() =>
            {
                try
                {
                    if (!this.Connect())
                        return;

                    var writer = new StreamWriter(client.GetStream()) { AutoFlush = true };
                    var reader = new StreamReader(client.GetStream());

                    writer.WriteLine(cmd);
                    var reply = ReadLine(reader);

                    subject.OnNext(reply);
                    
                    if (Regex.IsMatch(reply, "201.*"))
                        subject.OnNext(ReadLine(reader));
                    else if (Regex.IsMatch(reply, "200.*"))
                    {
                        while (reply != string.Empty)
                        {
                            reply = ReadLine(reader);
                            subject.OnNext(reply);
                        }
                    }
                    else if (Regex.IsMatch(reply, "400.*"))
                        throw new Exception("Command not understood.");
                    else if (Regex.IsMatch(reply, "401.*"))
                        throw new Exception("Illegal Command.");
                    else if (Regex.IsMatch(reply, "402.*"))
                        throw new Exception("Parameter missing.");
                    else if (Regex.IsMatch(reply, "403.*"))
                        throw new Exception("Illegal parameter.");
                    else if (Regex.IsMatch(reply, "404.*"))
                        throw new Exception("Media file not found.");
                    else if (Regex.IsMatch(reply, "500.*"))
                        throw new Exception("Internal server error.");
                    else if (Regex.IsMatch(reply, "501.*"))
                        throw new Exception("Internal server error.");
                    else if (Regex.IsMatch(reply, "502.*"))
                        throw new Exception("Media file unreadable.");
                }
                catch (IOException ex)
                {
                    this.Reset();
                    subject.OnError(ex);
                }
                catch(ObjectDisposedException ex)
                {
                    this.Reset();
                    subject.OnError(ex);
                }
                catch (Exception ex)
                {
                    subject.OnError(ex);
                }

                subject.OnCompleted();
            });
            
            return subject;
        }
    }
}
