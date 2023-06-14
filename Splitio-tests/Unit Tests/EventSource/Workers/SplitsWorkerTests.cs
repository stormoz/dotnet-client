﻿using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;
using Splitio.Domain;
using Splitio.Services.Cache.Interfaces;
using Splitio.Services.Common;
using Splitio.Services.EventSource;
using Splitio.Services.EventSource.Workers;
using Splitio.Services.Parsing.Interfaces;
using Splitio.Services.Shared.Classes;
using Splitio.Services.Shared.Interfaces;
using System.Collections.Concurrent;
using System.Threading;

namespace Splitio_Tests.Unit_Tests.EventSource.Workers
{
    [TestClass]
    public class SplitsWorkerTests
    {
        private readonly IWrapperAdapter wrapperAdapter = WrapperAdapter.Instance();

        private readonly Mock<ISynchronizer> _synchronizer;
        private readonly Mock<ISplitCache> _featureFlagCache;
        private readonly Mock<ISplitParser> _featureFlagParser;
        private readonly BlockingCollection<SplitChangeNotification> _queue;

        private readonly ISplitsWorker _splitsWorker;

        public SplitsWorkerTests()
        {
            _synchronizer = new Mock<ISynchronizer>();
            _featureFlagCache = new Mock<ISplitCache>();
            _featureFlagParser = new Mock<ISplitParser>();
            _queue = new BlockingCollection<SplitChangeNotification>(new ConcurrentQueue<SplitChangeNotification>());
            _splitsWorker = new SplitsWorker(_synchronizer.Object, new TasksManager(wrapperAdapter), _featureFlagCache.Object, _featureFlagParser.Object, _queue);
        }

        [TestMethod]
        public void AddToQueueWithOldChangeNumberShouldNotFetch()
        {
            // Arrange.
            _featureFlagCache
                .Setup(mock => mock.GetChangeNumber())
                .Returns(5);

            _splitsWorker.AddToQueue(new SplitChangeNotification
            {
                ChangeNumber = 2,
                PreviousChangeNumber = 1,
                FeatureFlag = new Split
                {
                    status = "ARCHIVED",
                    name = "mauro_ff",
                    defaultTreatment = "off"
                }
            });

            // Act.
            _splitsWorker.Start();
            Thread.Sleep(500);

            // Assert.
            _featureFlagCache.Verify(mock => mock.RemoveSplit(It.IsAny<string>()), Times.Never);
            _featureFlagCache.Verify(mock => mock.AddOrUpdate(It.IsAny<string>(), It.IsAny<SplitBase>()), Times.Never);
            _synchronizer.Verify(mock => mock.SynchronizeSplits(It.IsAny<long>()), Times.Never);
            _featureFlagCache.Verify(mock => mock.SetChangeNumber(It.IsAny<long>()), Times.Never);
        }

        [TestMethod]
        public void AddToQueueWithNewFormatAndSamePcnShouldUpdateInMemory()
        {
            // Arrange.
            _featureFlagCache
                .Setup(mock => mock.GetChangeNumber())
                .Returns(5);

            _featureFlagParser
                .Setup(mock => mock.Parse(It.IsAny<Split>()))
                .Returns(new ParsedSplit());

            _splitsWorker.AddToQueue(new SplitChangeNotification
            {
                ChangeNumber = 6,
                PreviousChangeNumber = 5,
                FeatureFlag = new Split
                {
                    status = "ACTIVE",
                    name = "mauro_ff",
                    defaultTreatment = "off"
                }
            });

            // Act.
            _splitsWorker.Start();
            Thread.Sleep(500);

            // Assert.
            _featureFlagCache.Verify(mock => mock.RemoveSplit(It.IsAny<string>()), Times.Never);
            _featureFlagCache.Verify(mock => mock.AddOrUpdate(It.IsAny<string>(), It.IsAny<SplitBase>()), Times.Once);
            _synchronizer.Verify(mock => mock.SynchronizeSplits(It.IsAny<long>()), Times.Never);
            _featureFlagCache.Verify(mock => mock.SetChangeNumber(It.IsAny<long>()), Times.Once);
        }

        [TestMethod]
        public void AddToQueueSamePcnShouldRemoveSplit()
        {
            // Arrange.
            _featureFlagCache
                .Setup(mock => mock.GetChangeNumber())
                .Returns(1);

            _featureFlagParser
                .Setup(mock => mock.Parse(It.IsAny<Split>()))
                .Returns((ParsedSplit)null);

            _splitsWorker.AddToQueue(new SplitChangeNotification
            {
                ChangeNumber = 2,
                PreviousChangeNumber = 1,
                FeatureFlag = new Split
                {
                    status = "ARCHIVED",
                    name = "mauro_ff",
                    defaultTreatment = "off"
                }
            });

            // Act.
            _splitsWorker.Start();
            Thread.Sleep(500);

            // Assert.
            _featureFlagCache.Verify(mock => mock.RemoveSplit("mauro_ff"), Times.Once);
            _featureFlagCache.Verify(mock => mock.SetChangeNumber(2), Times.Once);
            _featureFlagCache.Verify(mock => mock.AddOrUpdate(It.IsAny<string>(), It.IsAny<SplitBase>()), Times.Never);

        }

        [TestMethod]        
        public void AddToQueue_WithElements_ShouldTriggerFetch()
        {
            // Act.
            _splitsWorker.Start();
            _splitsWorker.AddToQueue(new SplitChangeNotification { ChangeNumber = 1585956698457 });
            _splitsWorker.AddToQueue(new SplitChangeNotification { ChangeNumber = 1585956698467 });
            _splitsWorker.AddToQueue(new SplitChangeNotification { ChangeNumber = 1585956698477 });
            _splitsWorker.AddToQueue(new SplitChangeNotification { ChangeNumber = 1585956698476 });
            Thread.Sleep(1000);

            _splitsWorker.Stop();
            _splitsWorker.AddToQueue(new SplitChangeNotification { ChangeNumber = 1585956698486 });
            Thread.Sleep(100);
            _splitsWorker.AddToQueue(new SplitChangeNotification { ChangeNumber = 1585956698496 });

            // Assert
            _synchronizer.Verify(mock => mock.SynchronizeSplits(It.IsAny<long>()), Times.Exactly(4));
        }

        [TestMethod]
        public void AddToQueue_WithoutElemts_ShouldNotTriggerFetch()
        {
            // Act.
            _splitsWorker.Start();
            Thread.Sleep(500);

            // Assert.
            _synchronizer.Verify(mock => mock.SynchronizeSplits(It.IsAny<long>()), Times.Never);
        }

        [TestMethod]
        public void Kill_ShouldTriggerFetch()
        {
            // Arrange.            
            var changeNumber = 1585956698457;
            var splitName = "split-test";
            var defaultTreatment = "off";

            _featureFlagCache
                .Setup(mock => mock.GetChangeNumber())
                .Returns(1585956698447);

            _splitsWorker.Start();

            // Act.            
            _splitsWorker.Kill(new SplitKillNotification
            {
                ChangeNumber = changeNumber,
                SplitName = splitName,
                DefaultTreatment = defaultTreatment
            });
            Thread.Sleep(1000);

            // Assert.
            _featureFlagCache.Verify(mock => mock.Kill(changeNumber, splitName, defaultTreatment), Times.Once);
        }

        [TestMethod]
        public void Kill_ShouldNotTriggerFetch()
        {
            // Arrange.            
            var changeNumber = 1585956698457;
            var splitName = "split-test";
            var defaultTreatment = "off";

            _featureFlagCache
                .Setup(mock => mock.GetChangeNumber())
                .Returns(1585956698467);

            _splitsWorker.Start();

            // Act.            
            _splitsWorker.Kill(new SplitKillNotification
            {
                ChangeNumber = changeNumber,
                SplitName = splitName,
                DefaultTreatment = defaultTreatment
            });
            Thread.Sleep(1000);

            // Assert.
            _featureFlagCache.Verify(mock => mock.Kill(changeNumber, splitName, defaultTreatment), Times.Never);
        }
    }
}
