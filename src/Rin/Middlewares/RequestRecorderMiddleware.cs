using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Rin.Channel;
using Rin.Core;
using Rin.Core.Event;
using Rin.Core.Record;
using Rin.Extensions;
using Rin.Features;
using Rin.Hubs;
using Rin.Hubs.HubClients;
using Rin.Hubs.Payloads;
using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Connections;

namespace Rin.Middlewares
{
    public class RequestRecorderMiddleware
    {
        private readonly RequestDelegate _next;
        private readonly IMessageEventBus<RequestEventMessage> _eventBus;
        private readonly IMessageEventBus<StoreBodyEventMessage> _eventBusStoreBody;
        private readonly IRinRequestRecordingFeatureAccessor _recordingFeatureAccessor;
        private readonly ILogger _logger;

        public const string EventSourceName = "Rin.Middlewares.RequestRecorderMiddleware";

        public RequestRecorderMiddleware(
            RequestDelegate next,
            IMessageEventBus<RequestEventMessage> eventBus,
            IMessageEventBus<StoreBodyEventMessage> eventBusStoreBody,
            RinChannel rinChannel,
            ILoggerFactory loggerFactory,
            IRinRequestRecordingFeatureAccessor recordingFeatureAccessor)
        {
            _next = next;
            _eventBus = eventBus;
            _eventBusStoreBody = eventBusStoreBody;
            _logger = loggerFactory.CreateLogger<RequestRecorderMiddleware>();
            _recordingFeatureAccessor = recordingFeatureAccessor;
        }

        public async Task InvokeAsync(HttpContext context, RinOptions options)
        {
            var request = context.Request;
            var response = context.Response;

            if (request.Path.StartsWithSegments(options.Inspector.MountPath) || (options.RequestRecorder.Excludes.Any(x => x.Invoke(request))))
            {
                await _next(context);
                return;
            }

            // Prepare AsyncLocals
            var timelineRoot = TimelineScope.Prepare();
            _recordingFeatureAccessor.SetValue(null);

            HttpRequestRecord? record = default;
            try
            {
                record = await PreprocessAsync(context, options, timelineRoot);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unhandled Exception was thrown until pre-processing");
            }
            
            try
            {
                await _next(context);
            }
            catch (Exception ex)
            {
                if (record != null)
                {
                    record.Exception = new ExceptionData(ex);
                }
                throw;
            }
            finally
            {
                try
                {
                    if (record != null)
                    {
                        await PostprocessAsync(context, options, record);
                    }
                }
                catch (Exception ex)
                {
                    var skipLogging = context.RequestAborted.IsCancellationRequested ||
                        (ex is IOException && ex.InnerException is ConnectionAbortedException);

                   if (!skipLogging)
                    {
                        _logger.LogError(ex, "Unhandled Exception was thrown until post-processing");
                    }
                }
            }
        }

        private async Task<HttpRequestRecord> PreprocessAsync(HttpContext context, RinOptions options, ITimelineScope timelineRoot)
        {
            var request = context.Request;
            var response = context.Response;

            var record = new HttpRequestRecord()
            {
                Id = Guid.NewGuid().ToString(),
                IsHttps = request.IsHttps,
                Host = request.Host.Value!,
                QueryString = request.QueryString.Value,
                Path = request.Path,
                Method = request.Method,
                RequestReceivedAt = DateTimeOffset.Now,
                RequestHeaders = request.Headers.ToDictionary(k => k.Key, v => v.Value),
                RemoteIpAddress = request.HttpContext.Connection.RemoteIpAddress,
                Timeline = timelineRoot,
            };

            // Set Rin recorder feature.
            var feature = new RinRequestRecordingFeature(record);;
            _recordingFeatureAccessor.SetValue(feature);
            context.Features.Set<IRinRequestRecordingFeature>(feature);

            await _eventBus.PostAsync(new RequestEventMessage(EventSourceName, record, RequestEvent.BeginRequest));

            // Set a current Rin request ID to response header.
            context.Response.Headers.Add("X-Rin-Request-Id", record.Id);

            if (options.RequestRecorder.EnableBodyCapturing)
            {
                context.EnableResponseDataCapturing();
                request.EnableBuffering();
            }
            response.OnStarting(OnStarting, record);
            response.OnCompleted(OnCompleted, record);

            // Execute pipeline middlewares.
            record.Processing = TimelineScope.Create("Processing", TimelineEventCategory.AspNetCoreCommon);

            return record;
        }

        private async Task PostprocessAsync(HttpContext context, RinOptions options, HttpRequestRecord record)
        {
            var request = context.Request;
            var response = context.Response;

            record.Processing.Complete();

            record.ResponseStatusCode = response.StatusCode;
            record.ResponseHeaders = response.Headers.ToDictionary(k => k.Key, v => v.Value);

            if (options.RequestRecorder.EnableBodyCapturing)
            {

                var memoryStreamRequestBody = new MemoryStream();
                request.Body.Position = 0; // rewind the stream to head
                await request.Body.CopyToAsync(memoryStreamRequestBody);

                await _eventBusStoreBody.PostAsync(new StoreBodyEventMessage(StoreBodyEvent.Request, record.Id, memoryStreamRequestBody.ToArray()));

                var feature = context.Features.Get<IRinRequestRecordingFeature>();
                if (feature?.ResponseDataStream != null)
                {
                    await _eventBusStoreBody.PostAsync(new StoreBodyEventMessage(StoreBodyEvent.Response, record.Id, feature.ResponseDataStream.GetCapturedData()));
                }
            }

            if (request.CheckTrailersAvailable())
            {
                if (request.CheckTrailersAvailable() && context.Features.Get<IHttpRequestTrailersFeature>() is { } trailers)
                {
                    record.RequestTrailers = trailers.Trailers.ToDictionary(k => k.Key, v => v.Value);
                }
            }
            if (response.SupportsTrailers())
            {
                if (response.SupportsTrailers() && context.Features.Get<IHttpResponseTrailersFeature>() is { } trailers)
                {
                    record.ResponseTrailers = trailers.Trailers.ToDictionary(k => k.Key, v => v.Value);
                }
            }

            var exceptionFeature = context.Features.Get<IExceptionHandlerFeature>();
            if (exceptionFeature != null)
            {
                record.Exception = new ExceptionData(exceptionFeature.Error);
            }
        }

        private Task OnStarting(object state)
        {
            var record = ((HttpRequestRecord)state);
            record.Transferring = TimelineScope.Create("Transferring", TimelineEventCategory.AspNetCoreCommon);
            return Task.CompletedTask;
        }

        private Task OnCompleted(object state)
        {
            var record = ((HttpRequestRecord)state);

            record.TransferringCompletedAt = DateTime.Now;
            record.Transferring?.Complete();
            record.Timeline.Complete();

            return _eventBus.PostAsync(new RequestEventMessage(EventSourceName, record, RequestEvent.CompleteRequest)).AsTask();
        }
    }
}
