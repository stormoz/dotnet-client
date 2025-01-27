﻿using System.Threading.Tasks;

namespace Splitio.Services.Common
{
    public interface IPushManager
    {
        Task StartAsync();
        Task StopAsync();
        Task ScheduleConnectionResetAsync();
    }
}
