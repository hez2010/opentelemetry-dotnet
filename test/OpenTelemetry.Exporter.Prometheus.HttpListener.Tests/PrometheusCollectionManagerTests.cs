// Copyright The OpenTelemetry Authors
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics.Metrics;
using OpenTelemetry.Metrics;
using OpenTelemetry.Tests;
using Xunit;

namespace OpenTelemetry.Exporter.Prometheus.Tests;

public sealed class PrometheusCollectionManagerTests
{
    [Theory]
    [InlineData(0, true)] // disable cache, default value for HttpListener
    [InlineData(0, false)] // disable cache, default value for HttpListener
#if PROMETHEUS_ASPNETCORE
    [InlineData(300, true)] // default value for AspNetCore, no possibility to set on HttpListener
    [InlineData(300, false)] // default value for AspNetCore, no possibility to set on HttpListener
#endif
    public async Task EnterExitCollectTest(int scrapeResponseCacheDurationMilliseconds, bool openMetricsRequested)
    {
        bool cacheEnabled = scrapeResponseCacheDurationMilliseconds != 0;
        using var meter = new Meter(Utils.GetCurrentMethodName());

        using (var provider = Sdk.CreateMeterProviderBuilder()
            .AddMeter(meter.Name)
#if PROMETHEUS_HTTP_LISTENER
            .AddPrometheusHttpListener()
#elif PROMETHEUS_ASPNETCORE
            .AddPrometheusExporter(x => x.ScrapeResponseCacheDurationMilliseconds = scrapeResponseCacheDurationMilliseconds)
#endif
            .Build())
        {
            if (!provider.TryFindExporter(out PrometheusExporter exporter))
            {
                throw new InvalidOperationException("PrometheusExporter could not be found on MeterProvider.");
            }

            int runningCollectCount = 0;
            var collectFunc = exporter.Collect;
            exporter.Collect = (timeout) =>
            {
                bool result = collectFunc(timeout);
                runningCollectCount++;
                Thread.Sleep(5000);
                return result;
            };

            var counter = meter.CreateCounter<int>("counter_int", description: "Prometheus help text goes here \n escaping.");
            counter.Add(100);

            Task<(Response Response, bool FromCache)>[] collectTasks = new Task<(Response, bool)>[10];
            for (int i = 0; i < collectTasks.Length; i++)
            {
                collectTasks[i] = Task.Run(async () =>
                {
                    var result = exporter.CollectionManager.EnterCollect();
                    var response = await result.Response;
                    try
                    {
                        return (new Response
                        {
                            CollectionResponse = response,
                            ViewPayload = openMetricsRequested ? response.OpenMetricsView.ToArray() : response.PlainTextView.ToArray(),
                        }, result.FromCache);
                    }
                    finally
                    {
                        exporter.CollectionManager.ExitCollect();
                    }
                });
            }

            await Task.WhenAll(collectTasks);

            Assert.Equal(1, runningCollectCount);

            var firstResponse = await collectTasks[0];

            Assert.False(firstResponse.FromCache);

            for (int i = 1; i < collectTasks.Length; i++)
            {
                Assert.Equal(firstResponse.Response.ViewPayload, (await collectTasks[i]).Response.ViewPayload);
                Assert.Equal(firstResponse.Response.CollectionResponse.GeneratedAtUtc, (await collectTasks[i]).Response.CollectionResponse.GeneratedAtUtc);
            }

            counter.Add(100);

            // This should use the cache and ignore the second counter update.
            var result = exporter.CollectionManager.EnterCollect();
            Assert.Equal(cacheEnabled, result.Response.IsCompleted);
            Assert.Equal(cacheEnabled, result.FromCache);

            var response = await result.Response;
            try
            {
                if (cacheEnabled)
                {
                    Assert.Equal(1, runningCollectCount);
                    Assert.True(result.FromCache);
                    Assert.Equal(firstResponse.Response.CollectionResponse.GeneratedAtUtc, response.GeneratedAtUtc);
                }
                else
                {
                    Assert.Equal(2, runningCollectCount);
                    Assert.False(result.FromCache);
                    Assert.True(firstResponse.Response.CollectionResponse.GeneratedAtUtc < response.GeneratedAtUtc);
                }
            }
            finally
            {
                exporter.CollectionManager.ExitCollect();
            }

            Thread.Sleep(exporter.ScrapeResponseCacheDurationMilliseconds);

            counter.Add(100);

            for (int i = 0; i < collectTasks.Length; i++)
            {
                collectTasks[i] = Task.Run(async () =>
                {
                    var result = exporter.CollectionManager.EnterCollect();
                    var response = await result.Response;
                    try
                    {
                        return (new Response
                        {
                            CollectionResponse = response,
                            ViewPayload = openMetricsRequested ? response.OpenMetricsView.ToArray() : response.PlainTextView.ToArray(),
                        }, result.FromCache);
                    }
                    finally
                    {
                        exporter.CollectionManager.ExitCollect();
                    }
                });
            }

            await Task.WhenAll(collectTasks);

            Assert.Equal(cacheEnabled ? 2 : 3, runningCollectCount);
            Assert.NotEqual(firstResponse.Response.ViewPayload, (await collectTasks[0]).Response.ViewPayload);
            Assert.NotEqual(firstResponse.Response.CollectionResponse.GeneratedAtUtc, (await collectTasks[0]).Response.CollectionResponse.GeneratedAtUtc);

            firstResponse = await collectTasks[0];

            Assert.False(firstResponse.FromCache);

            for (int i = 1; i < collectTasks.Length; i++)
            {
                Assert.Equal(firstResponse.Response.ViewPayload, (await collectTasks[i]).Response.ViewPayload);
                Assert.Equal(firstResponse.Response.CollectionResponse.GeneratedAtUtc, (await collectTasks[i]).Response.CollectionResponse.GeneratedAtUtc);
            }
        }
    }

    private class Response
    {
        public PrometheusCollectionManager.CollectionResponse CollectionResponse;

        public byte[] ViewPayload;
    }
}
