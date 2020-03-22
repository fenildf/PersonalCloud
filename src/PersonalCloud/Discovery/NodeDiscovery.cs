﻿using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.Extensions.Logging;

using Newtonsoft.Json;
using NSPersonalCloud.Interfaces.Errors;

namespace NSPersonalCloud
{
    enum NodeDiscoveryState
    {
        Listening,
        Created,
    }
    class NodeInfo
    {
        public string NodeGuid;
        public string PCVersion;
        public long TimeStamp;//sender timestamp
        public long LastSeenTime;//Last seen time
        public string  Url;
    }
    class NodeDiscovery : IDisposable
    {
        readonly ILoggerFactory loggerFactory;
        readonly ILogger logger;


        long TimeStamp;

        public NodeDiscoveryState State { get; internal set; }

        public event Action<NodeInfo> OnNodeAdded;//node guid,url

        public event EventHandler<ErrorCode> OnError;

        readonly UdpMulticastServer udpMulticastServer;
        private readonly Dictionary<IPAddress, SsdpServerProxy> _SocketProxies;
        System.Threading.Timer timer;
        public int RepublicTime = 10 * 60 * 1000;//10-3 s
        public int BindPort;
        public int[] TargetPort;

        public NodeDiscovery(ILoggerFactory logfac)
        {
            BindPort = Definition.MulticastPort;
            TargetPort = new[] { Definition.MulticastPort };
            loggerFactory = logfac;
            logger = loggerFactory.CreateLogger("NodeDiscovery");

            timer = new System.Threading.Timer(OnTimer,null, Timeout.Infinite, Timeout.Infinite);
            _SocketProxies = new Dictionary<IPAddress, SsdpServerProxy>();
            udpMulticastServer = new UdpMulticastServer(loggerFactory.CreateLogger("UdpMulticastServer"));


            State = NodeDiscoveryState.Created;
            TimeStamp = DateTime.UtcNow.ToFileTime();
        }


        void FillProxies(List<Tuple<IPAddress, int>>  ips)
        {
            lock (_SocketProxies)
            {
                foreach (var item in ips)
                {
                    if (_SocketProxies.ContainsKey(item.Item1))
                    {
                        continue;
                    }
                    int[] tp = TargetPort;
//                     if ((item.Item1==IPAddress.Any)|| (item.Item1 == IPAddress.IPv6Any))
//                     {
//                         tp = Array.Empty<int>();//disable send on 0.0.0.0 or ::
//                     }
                    var svr = new SsdpServerProxy(item.Item1, item.Item2, udpMulticastServer, 
                        loggerFactory.CreateLogger("SsdpServerProxy"),BindPort,tp);
                    svr.ResponseReceived += _BroadcastListener_DeviceAvailable;
                    svr.OnError += OnError;
                    _SocketProxies.Add(item.Item1, svr);
                    svr.BeginListeningForBroadcasts();
                }
            }
        }


        static List<Tuple<IPAddress, int>> GetLocalIPAddress()
        {
            var networkInterfaces = NetworkInterface.GetAllNetworkInterfaces();
            var ips = new List<Tuple<IPAddress, int>>();
            //Parallel.ForEach(networkInterfaces, new ParallelOptions() { MaxDegreeOfParallelism = Environment.ProcessorCount },
            foreach (var networkInterface in networkInterfaces)
            {
                if (networkInterface.OperationalStatus != OperationalStatus.Up)
                    continue;

                if (!networkInterface.SupportsMulticast)
                    continue;

                if (networkInterface.NetworkInterfaceType != NetworkInterfaceType.Ethernet &&
                    networkInterface.NetworkInterfaceType != NetworkInterfaceType.Wireless80211)
                    continue;
                AddUnicastAddress(networkInterface, ips);
            }
//             ips.Add(Tuple.Create(IPAddress.Any, 0));
//             ips.Add(Tuple.Create(IPAddress.IPv6Any, 0));
            return ips;
        }


        private static void AddUnicastAddress(NetworkInterface networkInterface, List<Tuple<IPAddress, int>> ips)
        {
            var interfaceProperties = networkInterface.GetIPProperties();
            var unicastAddresses = interfaceProperties.UnicastAddresses;

            int index = 0;
            try
            {
                var props = interfaceProperties.GetIPv4Properties();
                index = props.Index;
            }
            catch
            {
                try
                {
                    var props = interfaceProperties.GetIPv6Properties();
                    index = props.Index;
                }
                catch { }
            }

            foreach (var ipAddressInfo in unicastAddresses)
            {
                var ip = ipAddressInfo.Address;
                try
                {
                    if (ipAddressInfo.SuffixOrigin == System.Net.NetworkInformation.SuffixOrigin.Random)
                    {
                        continue;
                    }
                    if (ipAddressInfo.SuffixOrigin == System.Net.NetworkInformation.SuffixOrigin.WellKnown)
                    {
                        continue;
                    }
                }
                catch (NotImplementedException )
                {
                    try
                    {
                        if (IPAddress.IsLoopback(ip))
                        {
                            continue;
                        }
                    }
                    catch
                    {
                    }
                }
                if (((ip.AddressFamily == AddressFamily.InterNetwork) || (ip.AddressFamily == AddressFamily.InterNetworkV6)))
                {
                    lock (ips)
                    {
                        ips.Add(Tuple.Create(ip, index));
                    }
                    continue;
                }
            }

        }

        async Task<List<IPAddress>> GetExcludeIPAddress()
        {
            var ret = new List<IPAddress>();
            var ipGlobalProperties = System.Net.NetworkInformation.IPGlobalProperties.GetIPGlobalProperties();
            foreach (var item in await ipGlobalProperties.GetUnicastAddressesAsync().ConfigureAwait(false))
            {
                if (item.SuffixOrigin == System.Net.NetworkInformation.SuffixOrigin.Random)
                {
                    ret.Add(item.Address);
                    continue;
                }
                if (item.SuffixOrigin == System.Net.NetworkInformation.SuffixOrigin.WellKnown)
                {
                    ret.Add(item.Address);
                    continue;
                }
                var ip = item.Address;
                var ipbytes = ip.GetAddressBytes();
                if ((ipbytes[0] >>1)==1 )//2000::/3, global unicase ip
                {
                    ret.Add(item.Address);
                    continue;
                }
            }
            return ret;
        }



        private void OnTimer(object state)
        {
            lock (_SocketProxies)
            {
                foreach (var item in _SocketProxies)
                {
                    item.Value.SendAnnounce();
                }
            }
        }

        public Task RePublish(string nodeguid,int webserverport)
        {
            logger.LogDebug($"{webserverport} is going to RePublish");

            Interlocked.Increment(ref TimeStamp);

            var ips = GetLocalIPAddress();
            FillProxies(ips);

            lock (_SocketProxies)
            {
                foreach (var item in _SocketProxies)
                {
                    try
                    {
                        var ip = item.Key;
                        string url = null;
                        if (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6)
                        {
                            url = $"http://[{ip}]:{webserverport}/";
                        }
                        else
                        {
                            url = $"http://{ip}:{webserverport}/";
                        }

                        var node = new NodeInfo {
                            NodeGuid = nodeguid,
                            PCVersion = Definition.CloudVersion.ToString(CultureInfo.InvariantCulture),
                            TimeStamp = TimeStamp,
                            Url = url,
                        };
                        item.Value.AnnounceString = JsonConvert.SerializeObject(node);

                    }
                    catch (Exception e)
                    {
                        logger.LogError($"Error {e.Message} {e.StackTrace}");
                    }
                }
            }
            timer.Change(0, RepublicTime);
            return Task.CompletedTask;

        }

        public void ForceNetworkRefesh()
        {
            timer.Change(0, RepublicTime);
        }

        public void StopNetwork()
        {
            timer.Change(Timeout.Infinite, Timeout.Infinite);
            CleaEndPoints();
        }

        void CleaEndPoints()
        {
            lock (_SocketProxies)
            {
                foreach (var item in _SocketProxies)
                {
                    item.Value?.Dispose();
                }
                _SocketProxies.Clear();
            }
        }
        //not thread safe
        public void StartMonitoring()
        {
            if (State== NodeDiscoveryState.Listening)
            {
                return;
            }
            CleaEndPoints();
            var ips = GetLocalIPAddress();
            FillProxies(ips);

            lock (_SocketProxies)
            {
                foreach (var item in _SocketProxies)
                {
                    item.Value.Search();
                }
            }
            State = NodeDiscoveryState.Listening;
        }

        private void _BroadcastListener_DeviceAvailable(object _, string content)
        {
            try
            {
                //logger.LogDebug("_BroadcastListener_DeviceAvailable called");

                var node = JsonConvert.DeserializeObject<NodeInfo>(content);

                if ((node==null)||string.IsNullOrWhiteSpace(node.NodeGuid))
                {
                    logger.LogError($"_BroadcastListener_DeviceAvailable receive invalid content {content}");
                    return;
                }
                OnNodeAdded?.Invoke(node);
            }
            catch (Exception ex)
            {
                logger.LogError($"{ex.Message} {ex.StackTrace}");
            }
        }


        public void StartSearch()
        {
            lock (_SocketProxies)
            {
                foreach (var item in _SocketProxies)
                {
                    item.Value.Search();
                }
            }
        }


        protected virtual void Dispose(bool disposing)
        {
            if (disposing)
            {
                timer?.Dispose();
                CleaEndPoints();
            }
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

    }
}