using System;
using System.Net;
using System.IO;
using System.Drawing;
using Bespoke.Common.Osc;
using System.Linq;

namespace Client
{
	class Program
	{
		static void Main(string[] args)
		{
            OscPacket.LittleEndianByteOrder = false;
            sOscServer = new OscServer(TransportType.Udp, IPAddress.Parse("127.0.0.1"), 6250);
            sOscServer.BundleReceived += new OscBundleReceivedHandler(sOscServer_BundleReceived);
			sOscServer.MessageReceived += new OscMessageReceivedHandler(sOscServer_MessageReceived);
            sOscServer.FilterRegisteredMethods = false;

			sOscServer.Start();

			Console.WriteLine("OSC Client: " + sOscServer.TransmissionType.ToString());
			Console.WriteLine("Press any key to exit.");
			Console.ReadKey();
			sOscServer.Stop();
		}

        static void sOscServer_BundleReceived(object sender, OscBundleReceivedEventArgs e)
        {
            Console.WriteLine(string.Format("\nBundle Received [{0}]: {1} Message Count: {2}", e.Bundle.SourceEndPoint.Address, e.Bundle.Address, e.Bundle.Messages.Length));
        }

		static void sOscServer_MessageReceived(object sender, OscMessageReceivedEventArgs e)
		{
            Console.Write(string.Format("Message Received [{0}]: {1} ", e.Message.SourceEndPoint.Address, e.Message.Address));
            e.Message.Data.ToList().ForEach(i => Console.Write("{0} ", i));
            Console.WriteLine();
		}

		private static OscServer sOscServer;
	}
}
