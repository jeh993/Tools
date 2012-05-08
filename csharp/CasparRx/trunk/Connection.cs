﻿/*
* Copyright (c) 2012 Robert Nagy and Thomas III Kaltz
*
* This file is part of CasparRX Framework).
*
* CasparRX is free software: you can redistribute it and/or modify
* it under the terms of the GNU General Public License as published by
* the Free Software Foundation, either version 3 of the License, or
* (at your option) any later version.
*
* CasparRX is distributed in the hope that it will be useful,
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

namespace CasparRx
{
    public class Connection : IDisposable
    {
        private string  host;
        private int     port;

        private CompositeDisposable     disposables = new CompositeDisposable();
        private BehaviorSubject<bool>   connectedSubject = new BehaviorSubject<bool>(false);
        private TcpClient               client = new TcpClient();
        private EventLoopScheduler      scheduler = new EventLoopScheduler(ts => new Thread(ts));

        public Connection(string host = "localhost", int port = 5250)
        {
            this.host = host;
            this.port = port;

            this.disposables.Add(scheduler);
            this.disposables.Add(Observable
                .Interval(TimeSpan.FromSeconds(1))
                .ObserveOn(scheduler)
                .Subscribe(x =>
                {
                    try
                    {
                        this.Connect();
                    }
                    catch
                    {
                        // Failed to connect... try again.
                    }
                }));
        }

        public IObservable<string> Send(string cmd)
        {
            var subject = new AsyncSubject<string>();
            
            this.scheduler.Schedule(() =>
            {
                try
                {
                    this.Connect();
                    
                    var writer = new StreamWriter(client.GetStream()) { AutoFlush = true };
                    var reader = new StreamReader(client.GetStream());
                    
                    writer.WriteLine(cmd);
                    var reply = reader.ReadLine();

                    subject.OnNext(reply);

                    if (Regex.IsMatch(reply, "201.*"))
                        subject.OnNext(reader.ReadLine());
                    else if (Regex.IsMatch(reply, "200.*"))
                    {
                        while (reply != string.Empty)
                        {
                            reply = reader.ReadLine();
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
                catch (Exception ex)
                {
                    subject.OnError(ex);
                }

                subject.OnCompleted();
            });
            
            return subject;
        }

        public IObservable<bool> OnConnected
        {
            get { return this.connectedSubject.DistinctUntilChanged(); }
        }

        private void Connect()
        {
            if (this.client.Connected)
                return;

            try
            {
                this.client.Close();
                this.client = new TcpClient();
                this.client.Connect(host, port);
            }
            finally
            {
                this.connectedSubject.OnNext(this.client.Connected);
            }
        }

        public void Dispose()
        {
            this.Send("BYE");
            this.disposables.Dispose();
            this.client.Close();
        }
    }
}