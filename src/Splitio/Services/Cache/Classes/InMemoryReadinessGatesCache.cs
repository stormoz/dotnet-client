﻿using Splitio.Services.Cache.Interfaces;
using Splitio.Services.Logger;
using Splitio.Services.Shared.Classes;
using System.Threading;

namespace Splitio.Services.Client.Classes
{
    public class InMemoryReadinessGatesCache : IReadinessGatesCache
    {
        private static readonly ISplitLogger _log = WrapperAdapter.GetLogger(typeof(InMemoryReadinessGatesCache));
        private readonly CountdownEvent _sdkReady = new CountdownEvent(1);

        public bool IsReady()
        {
            return _sdkReady.IsSet;
        }

        public bool WaitUntilReady(int milliseconds)
        {
            return _sdkReady.Wait(milliseconds);
        }

        public void SetReady()
        {
            _sdkReady.Signal();
        }
    }
}
