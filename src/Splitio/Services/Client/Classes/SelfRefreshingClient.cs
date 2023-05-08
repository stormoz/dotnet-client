﻿using Splitio.CommonLibraries;
using Splitio.Domain;
using Splitio.Services.Cache.Classes;
using Splitio.Services.Common;
using Splitio.Services.Events.Classes;
using Splitio.Services.Events.Interfaces;
using Splitio.Services.EventSource;
using Splitio.Services.EventSource.Workers;
using Splitio.Services.Impressions.Classes;
using Splitio.Services.Impressions.Interfaces;
using Splitio.Services.InputValidation.Classes;
using Splitio.Services.Parsing.Classes;
using Splitio.Services.SegmentFetcher.Classes;
using Splitio.Services.SegmentFetcher.Interfaces;
using Splitio.Services.Shared.Classes;
using Splitio.Services.SplitFetcher.Classes;
using Splitio.Services.SplitFetcher.Interfaces;
using Splitio.Telemetry.Common;
using Splitio.Telemetry.Storages;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;

namespace Splitio.Services.Client.Classes
{
    public class SelfRefreshingClient : SplitClient
    {
        private readonly SelfRefreshingConfig _config;

        /// <summary>
        /// Represents the initial number of buckets for a ConcurrentDictionary. 
        /// Should not be divisible by a small prime number. 
        /// The default capacity is 31. 
        /// More details : https://msdn.microsoft.com/en-us/library/dd287171(v=vs.110).aspx
        /// </summary>
        private const int InitialCapacity = 31;
        private readonly long _startSessionMs;

        private ISplitFetcher _splitFetcher;
        private ISplitSdkApiClient _splitSdkApiClient;
        private ISegmentSdkApiClient _segmentSdkApiClient;
        private IImpressionsSdkApiClient _impressionsSdkApiClient;
        private IEventSdkApiClient _eventSdkApiClient;
        private ISelfRefreshingSegmentFetcher _selfRefreshingSegmentFetcher;
        private ISyncManager _syncManager;
        private ITelemetrySyncTask _telemetrySyncTask;        
        private ITelemetryStorageConsumer _telemetryStorageConsumer;
        private ITelemetryRuntimeProducer _telemetryRuntimeProducer;
        private ITelemetryAPI _telemetryAPI; 

        public SelfRefreshingClient(string apiKey, ConfigurationOptions config) : base()
        {
            _config = (SelfRefreshingConfig)_configService.ReadConfig(config, ConfingTypes.InMemory);
            LabelsEnabled = _config.LabelsEnabled;
            
            ApiKey = apiKey;
            BuildSplitCache();
            BuildSegmentCache();
            BuildTelemetryStorage();
            BuildTelemetrySyncTask();
            BuildSdkApiClients();
            BuildSplitFetcher();
            BuildTreatmentLog(config);

            BuildSenderAdapter();
            BuildUniqueKeysTracker(_config);
            BuildImpressionsCounter(_config);
            BuildImpressionsObserver();
            BuildImpressionManager();

            BuildEventLog(config);
            BuildEvaluator();
            BuildBlockUntilReadyService();
            BuildManager();
            BuildSyncManager();

            _startSessionMs = CurrentTimeHelper.CurrentTimeMillis();
            Start();
        }

        #region Public Methods
        public override void Destroy()
        {
            if (!_statusManager.IsDestroyed())
            {
                _telemetryRuntimeProducer.RecordSessionLength(CurrentTimeHelper.CurrentTimeMillis() - _startSessionMs);
                Stop();
                base.Destroy();
            }
        }
        #endregion

        #region Private Methods
        private void BuildSplitCache()
        {
            _splitCache = new InMemorySplitCache(new ConcurrentDictionary<string, ParsedSplit>(_config.ConcurrencyLevel, InitialCapacity));
        }

        private void BuildSegmentCache()
        {
            _segmentCache = new InMemorySegmentCache(new ConcurrentDictionary<string, Segment>(_config.ConcurrencyLevel, InitialCapacity));
        }

        private void BuildTelemetryStorage()
        {
            var telemetryStorage = new InMemoryTelemetryStorage();

            _telemetryStorageConsumer = telemetryStorage;
            _telemetryEvaluationProducer = telemetryStorage;
            _telemetryInitProducer = telemetryStorage;
            _telemetryRuntimeProducer = telemetryStorage;
        }

        private void BuildSplitFetcher()
        {
            var segmentRefreshRate = _config.RandomizeRefreshRates ? Random(_config.SegmentRefreshRate) : _config.SegmentRefreshRate;
            var segmentChangeFetcher = new ApiSegmentChangeFetcher(_segmentSdkApiClient);
            var segmentTaskQueue = new SegmentTaskQueue();
            _selfRefreshingSegmentFetcher = new SelfRefreshingSegmentFetcher(segmentChangeFetcher, _statusManager, segmentRefreshRate, _segmentCache, _config.NumberOfParalellSegmentTasks, segmentTaskQueue, _tasksManager, _wrapperAdapter);

            var splitChangeFetcher = new ApiSplitChangeFetcher(_splitSdkApiClient);
            var splitsRefreshRate = _config.RandomizeRefreshRates ? Random(_config.SplitsRefreshRate) : _config.SplitsRefreshRate;
            _splitParser = new InMemorySplitParser((SelfRefreshingSegmentFetcher)_selfRefreshingSegmentFetcher, _segmentCache);            
            _splitFetcher = new SelfRefreshingSplitFetcher(splitChangeFetcher, _splitParser, _statusManager, splitsRefreshRate, _tasksManager, _splitCache);
            _trafficTypeValidator = new TrafficTypeValidator(_splitCache);
        }

        private void BuildTreatmentLog(ConfigurationOptions config)
        {
            var impressionsCache = new InMemorySimpleCache<KeyImpression>(new BlockingQueue<KeyImpression>(_config.TreatmentLogSize));
            _impressionsLog = new ImpressionsLog(_impressionsSdkApiClient, _config.TreatmentLogRefreshRate, impressionsCache, _tasksManager);

            _customerImpressionListener = config.ImpressionListener;
        }

        private void BuildSenderAdapter()
        {
            _impressionsSenderAdapter = new InMemorySenderAdapter(_telemetryAPI, _impressionsSdkApiClient);
        }

        private void BuildImpressionsObserver()
        {
            if (_config.ImpressionsMode == ImpressionsMode.None)
            {
                _impressionsObserver = new NoopImpressionsObserver();
                return;
            }

            var impressionHasher = new ImpressionHasher();
            _impressionsObserver = new ImpressionsObserver(impressionHasher);
        }

        private void BuildImpressionManager()
        {
            _impressionsManager = new ImpressionsManager(_impressionsLog, _customerImpressionListener, _impressionsCounter, true, _config.ImpressionsMode, _telemetryRuntimeProducer, _tasksManager, _uniqueKeysTracker, _impressionsObserver);
        }

        private void BuildEventLog(ConfigurationOptions config)
        {
            var eventsCache = new InMemorySimpleCache<WrappedEvent>(new BlockingQueue<WrappedEvent>(_config.EventLogSize));
            _eventsLog = new EventsLog(_eventSdkApiClient, _config.EventsFirstPushWindow, _config.EventLogRefreshRate, eventsCache, _telemetryRuntimeProducer, _tasksManager);
        }

        private int Random(int refreshRate)
        {
            Random random = new Random();
            return Math.Max(5, random.Next(refreshRate / 2, refreshRate));
        }

        private void BuildSdkApiClients()
        {
            var headers = GetHeaders();
            headers.Add(Constants.Http.AcceptEncoding, Constants.Http.Gzip);
            headers.Add(Constants.Http.KeepAlive, "true");

            var sdkHttpClient = new SplitioHttpClient(ApiKey, _config, headers);
            _splitSdkApiClient = new SplitSdkApiClient(sdkHttpClient, _telemetryRuntimeProducer, _config.BaseUrl);

            var segmentsHttpClient = new SplitioHttpClient(ApiKey, _config, headers);
            _segmentSdkApiClient = new SegmentSdkApiClient(segmentsHttpClient, _telemetryRuntimeProducer, _config.BaseUrl);

            var impressionsHttpClient = new SplitioHttpClient(ApiKey, _config, headers);
            _impressionsSdkApiClient = new ImpressionsSdkApiClient(impressionsHttpClient, _telemetryRuntimeProducer, _config.EventsBaseUrl, _wrapperAdapter, _config.ImpressionsBulkSize);

            var eventsHttpClient = new SplitioHttpClient(ApiKey, _config, headers);
            _eventSdkApiClient = new EventSdkApiClient(eventsHttpClient, _telemetryRuntimeProducer, _tasksManager, _wrapperAdapter, _config.EventsBaseUrl, _config.EventsBulkSize);
        }

        private void BuildManager()
        {
            _manager = new SplitManager(_splitCache, _blockUntilReadyService);
        }

        private void BuildBlockUntilReadyService()
        {
            _blockUntilReadyService = new SelfRefreshingBlockUntilReadyService(_statusManager, _telemetryInitProducer);
        }

        private void BuildTelemetrySyncTask()
        {
            var httpClient = new SplitioHttpClient(ApiKey, _config, GetHeaders());

            _telemetryAPI = new TelemetryAPI(httpClient, _config.TelemetryServiceURL, _telemetryRuntimeProducer);
            _telemetrySyncTask = new TelemetrySyncTask(_telemetryStorageConsumer, _telemetryAPI, _splitCache, _segmentCache, _config, FactoryInstantiationsService.Instance(), _wrapperAdapter, _tasksManager);
        }

        private void BuildSyncManager()
        {
            try
            {
                // Synchronizer
                var backOff = new BackOff(backOffBase: 10, attempt: 0, maxAllowed: 60);
                var synchronizer = new Synchronizer(_splitFetcher, _selfRefreshingSegmentFetcher, _impressionsLog, _eventsLog, _impressionsCounter, _wrapperAdapter, _statusManager, _telemetrySyncTask, _tasksManager, _splitCache, backOff, _config.OnDemandFetchMaxRetries, _config.OnDemandFetchRetryDelayMs, _segmentCache, _uniqueKeysTracker);

                // Workers
                var splitsWorker = new SplitsWorker(synchronizer, _tasksManager);
                var segmentsWorker = new SegmentsWorker(synchronizer, _tasksManager);

                // NotificationProcessor
                var notificationProcessor = new NotificationProcessor(splitsWorker, segmentsWorker, _splitCache);

                // NotificationParser
                var notificationParser = new NotificationParser();

                // SSEClient Status actions queue
                var sseClientStatus = new BlockingCollection<SSEClientActions>(new ConcurrentQueue<SSEClientActions>());

                // NotificationManagerKeeper
                var notificationManagerKeeper = new NotificationManagerKeeper(_telemetryRuntimeProducer, sseClientStatus);

                // EventSourceClient
                var headers = GetHeaders();
                headers.Add(Constants.Http.SplitSDKClientKey, ApiKey.Substring(ApiKey.Length - 4));
                var sseHttpClient = new SplitioHttpClient(ApiKey, _config, headers);
                var eventSourceClient = new EventSourceClient(notificationParser, sseHttpClient, _telemetryRuntimeProducer, _tasksManager, sseClientStatus);

                // SSEHandler
                var sseHandler = new SSEHandler(_config.StreamingServiceURL, splitsWorker, segmentsWorker, notificationProcessor, notificationManagerKeeper, eventSourceClient: eventSourceClient);

                // AuthApiClient
                var httpClient = new SplitioHttpClient(ApiKey, _config, GetHeaders());
                var authApiClient = new AuthApiClient(_config.AuthServiceURL, httpClient, _telemetryRuntimeProducer);

                // PushManager
                var backoff = new BackOff(_config.AuthRetryBackoffBase, attempt: 1);
                var pushManager = new PushManager(sseHandler, authApiClient, _wrapperAdapter, _telemetryRuntimeProducer, backoff);

                // SyncManager
                _syncManager = new SyncManager(_config.StreamingEnabled, synchronizer, pushManager, sseHandler, _telemetryRuntimeProducer, _statusManager, _tasksManager, _wrapperAdapter, _telemetrySyncTask, sseClientStatus);
            }
            catch (Exception ex)
            {
                _log.Error($"BuildSyncManager: {ex.Message}");
            }
        }        

        private Dictionary<string, string> GetHeaders()
        {
            var headers = new Dictionary<string, string>
            {
                { Constants.Http.SplitSDKVersion, _config.SdkVersion },
                { Constants.Http.SplitSDKImpressionsMode, _config.ImpressionsMode.ToString() }
            };

            if (!string.IsNullOrEmpty(_config.SdkMachineName) && !_config.SdkMachineName.Equals(Constants.Gral.Unknown))
            {
                headers.Add(Constants.Http.SplitSDKMachineName, _config.SdkMachineName);
            }

            if (!string.IsNullOrEmpty(_config.SdkMachineIP) && !_config.SdkMachineIP.Equals(Constants.Gral.Unknown))
            {
                headers.Add(Constants.Http.SplitSDKMachineIP, _config.SdkMachineIP);
            }

            return headers;
        }

        private void Start()
        {
            _syncManager.Start();
        }

        private void Stop()
        {
            _syncManager.Shutdown();
        }
        #endregion
    }
}
