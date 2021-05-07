// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Pipelines;
using System.Linq;
using System.Net.Http;
using System.Net.Http.QPack;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Connections.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Server.Kestrel.Core.Internal;
using Microsoft.AspNetCore.Server.Kestrel.Core.Internal.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core.Internal.Http3;
using Microsoft.AspNetCore.Server.Kestrel.Core.Internal.Infrastructure;
using Microsoft.AspNetCore.Testing;
using Microsoft.Extensions.Logging;
using Microsoft.Net.Http.Headers;
using Moq;
using Xunit;
using Xunit.Abstractions;
using static System.IO.Pipelines.DuplexPipe;
using static Microsoft.AspNetCore.Server.Kestrel.Core.Tests.Http2TestBase;

namespace Microsoft.AspNetCore.Server.Kestrel.Core.Tests
{
    public abstract class Http3TestBase : TestApplicationErrorLoggerLoggedTest, IDisposable
    {
        protected static readonly int MaxRequestHeaderFieldSize = 16 * 1024;
        protected static readonly string _4kHeaderValue = new string('a', 4096);
        protected static readonly byte[] _helloWorldBytes = Encoding.ASCII.GetBytes("hello, world");
        protected static readonly byte[] _maxData = Encoding.ASCII.GetBytes(new string('a', 16 * 1024));

        internal TestServiceContext _serviceContext;
        internal HttpConnection _httpConnection;
        internal readonly TimeoutControl _timeoutControl;
        internal readonly Mock<IKestrelTrace> _mockKestrelTrace = new Mock<IKestrelTrace>();
        internal readonly Mock<ITimeoutHandler> _mockTimeoutHandler = new Mock<ITimeoutHandler>();
        internal readonly Mock<MockTimeoutControlBase> _mockTimeoutControl;
        internal readonly MemoryPool<byte> _memoryPool = PinnedBlockMemoryPoolFactory.Create();
        protected Task _connectionTask;
        protected readonly TaskCompletionSource _closedStateReached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        internal readonly ConcurrentDictionary<long, Http3StreamBase> _runningStreams = new ConcurrentDictionary<long, Http3StreamBase>();
        protected readonly RequestDelegate _noopApplication;
        protected readonly RequestDelegate _echoApplication;
        protected readonly RequestDelegate _readRateApplication;
        protected readonly RequestDelegate _echoMethod;
        protected readonly RequestDelegate _echoPath;
        protected readonly RequestDelegate _echoHost;

        private Http3ControlStream _inboundControlStream;
        private long _currentStreamId;

        protected static readonly IEnumerable<KeyValuePair<string, string>> _browserRequestHeaders = new[]
        {
            new KeyValuePair<string, string>(HeaderNames.Method, "GET"),
            new KeyValuePair<string, string>(HeaderNames.Path, "/"),
            new KeyValuePair<string, string>(HeaderNames.Scheme, "http"),
            new KeyValuePair<string, string>(HeaderNames.Authority, "localhost:80"),
            new KeyValuePair<string, string>("user-agent", "Mozilla/5.0 (X11; Ubuntu; Linux x86_64; rv:54.0) Gecko/20100101 Firefox/54.0"),
            new KeyValuePair<string, string>("accept", "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8"),
            new KeyValuePair<string, string>("accept-language", "en-US,en;q=0.5"),
            new KeyValuePair<string, string>("accept-encoding", "gzip, deflate, br"),
            new KeyValuePair<string, string>("upgrade-insecure-requests", "1"),
        };

        protected static IEnumerable<KeyValuePair<string, string>> ReadRateRequestHeaders(int expectedBytes) => new[]
        {
            new KeyValuePair<string, string>(HeaderNames.Method, "POST"),
            new KeyValuePair<string, string>(HeaderNames.Path, "/" + expectedBytes),
            new KeyValuePair<string, string>(HeaderNames.Scheme, "http"),
            new KeyValuePair<string, string>(HeaderNames.Authority, "localhost:80"),
        };

        public Http3TestBase()
        {
            _timeoutControl = new TimeoutControl(_mockTimeoutHandler.Object);
            _mockTimeoutControl = new Mock<MockTimeoutControlBase>(_timeoutControl) { CallBase = true };
            _timeoutControl.Debugger = Mock.Of<IDebugger>();

            _mockKestrelTrace
                .Setup(m => m.Http3ConnectionClosed(It.IsAny<string>(), It.IsAny<long>()))
                .Callback(() => _closedStateReached.SetResult());


            _noopApplication = context => Task.CompletedTask;

            _echoApplication = async context =>
            {
                var buffer = new byte[16 * 1024];
                var received = 0;

                while ((received = await context.Request.Body.ReadAsync(buffer, 0, buffer.Length)) > 0)
                {
                    await context.Response.Body.WriteAsync(buffer, 0, received);
                }
            };

            _readRateApplication = async context =>
            {
                var expectedBytes = int.Parse(context.Request.Path.Value.Substring(1), CultureInfo.InvariantCulture);

                var buffer = new byte[16 * 1024];
                var received = 0;

                while (received < expectedBytes)
                {
                    received += await context.Request.Body.ReadAsync(buffer, 0, buffer.Length);
                }

                var stalledReadTask = context.Request.Body.ReadAsync(buffer, 0, buffer.Length);

                // Write to the response so the test knows the app started the stalled read.
                await context.Response.Body.WriteAsync(new byte[1], 0, 1);

                await stalledReadTask;
            };

            _echoMethod = context =>
            {
                context.Response.Headers["Method"] = context.Request.Method;

                return Task.CompletedTask;
            };

            _echoPath = context =>
            {
                context.Response.Headers["path"] = context.Request.Path.ToString();
                context.Response.Headers["rawtarget"] = context.Features.Get<IHttpRequestFeature>().RawTarget;

                return Task.CompletedTask;
            };

            _echoHost = context =>
            {
                context.Response.Headers.Host = context.Request.Headers.Host;

                return Task.CompletedTask;
            };
        }

        internal Http3Connection Connection { get; private set; }

        internal Http3ControlStream OutboundControlStream { get; private set; }

        public TestMultiplexedConnectionContext MultiplexedConnectionContext { get; set; }

        public override void Initialize(TestContext context, MethodInfo methodInfo, object[] testMethodArguments, ITestOutputHelper testOutputHelper)
        {
            base.Initialize(context, methodInfo, testMethodArguments, testOutputHelper);

            _serviceContext = new TestServiceContext(LoggerFactory, _mockKestrelTrace.Object)
            {
                Scheduler = PipeScheduler.Inline,
            };
        }

        internal long GetStreamId(long mask)
        {
            var id = (_currentStreamId << 2) | mask;

            _currentStreamId += 1;

            return id;
        }

        internal async ValueTask<Http3ControlStream> GetInboundControlStream()
        {
            if (_inboundControlStream == null)
            {
                var reader = MultiplexedConnectionContext.ToClientAcceptQueue.Reader;
                while (await reader.WaitToReadAsync())
                {
                    while (reader.TryRead(out var stream))
                    {
                        _inboundControlStream = stream;
                        var streamId = await stream.TryReadStreamIdAsync();
                        Debug.Assert(streamId == 0, "StreamId sent that was non-zero, which isn't handled by tests");
                        return _inboundControlStream;
                    }
                }
            }

            return _inboundControlStream;
        }

        internal void CloseConnectionGracefully()
        {
            MultiplexedConnectionContext.ConnectionClosingCts.Cancel();
        }

        internal Task WaitForConnectionStopAsync(long expectedLastStreamId, bool ignoreNonGoAwayFrames, Http3ErrorCode? expectedErrorCode = null)
        {
            return WaitForConnectionErrorAsync<Exception>(ignoreNonGoAwayFrames, expectedLastStreamId, expectedErrorCode: expectedErrorCode ?? 0, expectedErrorMessage: null);
        }

        internal async Task WaitForConnectionErrorAsync<TException>(bool ignoreNonGoAwayFrames, long? expectedLastStreamId, Http3ErrorCode expectedErrorCode, params string[] expectedErrorMessage)
            where TException : Exception
        {
            var frame = await _inboundControlStream.ReceiveFrameAsync();

            if (ignoreNonGoAwayFrames)
            {
                while (frame.Type != Http3FrameType.GoAway)
                {
                    frame = await _inboundControlStream.ReceiveFrameAsync();
                }
            }

            if (expectedLastStreamId != null)
            {
                VerifyGoAway(frame, expectedLastStreamId.GetValueOrDefault());
            }

            Assert.Equal((Http3ErrorCode)expectedErrorCode, (Http3ErrorCode)MultiplexedConnectionContext.Error);

            if (expectedErrorMessage?.Length > 0)
            {
                var message = Assert.Single(LogMessages, m => m.Exception is TException);

                Assert.Contains(expectedErrorMessage, expected => message.Exception.Message.Contains(expected));
            }
        }

        internal void VerifyGoAway(Http3FrameWithPayload frame, long expectedLastStreamId)
        {
            Assert.Equal(Http3FrameType.GoAway, frame.Type);
            var payload = frame.Payload;
            Assert.True(VariableLengthIntegerHelper.TryRead(payload.Span, out var streamId, out var _));
            Assert.Equal(expectedLastStreamId, streamId);
        }

        protected void AdvanceClock(TimeSpan timeSpan)
        {
            var clock = _serviceContext.MockSystemClock;
            var endTime = clock.UtcNow + timeSpan;

            while (clock.UtcNow + Heartbeat.Interval < endTime)
            {
                clock.UtcNow += Heartbeat.Interval;
                _timeoutControl.Tick(clock.UtcNow);
            }

            clock.UtcNow = endTime;
            _timeoutControl.Tick(clock.UtcNow);
        }

        protected void TriggerTick(DateTimeOffset now)
        {
            _serviceContext.MockSystemClock.UtcNow = now;
            Connection?.Tick(now);
        }

        protected async Task InitializeConnectionAsync(RequestDelegate application)
        {
            MultiplexedConnectionContext = new TestMultiplexedConnectionContext(this);

            var httpConnectionContext = new HttpMultiplexedConnectionContext(
                connectionId: "TestConnectionId",
                connectionContext: MultiplexedConnectionContext,
                connectionFeatures: MultiplexedConnectionContext.Features,
                serviceContext: _serviceContext,
                memoryPool: _memoryPool,
                localEndPoint: null,
                remoteEndPoint: null);
            httpConnectionContext.TimeoutControl = _mockTimeoutControl.Object;

            _httpConnection = new HttpConnection(httpConnectionContext);
            _httpConnection.Initialize(Connection);
            _mockTimeoutHandler.Setup(h => h.OnTimeout(It.IsAny<TimeoutReason>()))
                               .Callback<TimeoutReason>(r => _httpConnection.OnTimeout(r));

            // ProcessRequestAsync will create the Http3Connection
            _connectionTask = _httpConnection.ProcessRequestsAsync(new DummyApplication(application));

            Connection = (Http3Connection)_httpConnection._requestProcessor;
            Connection._streamLifetimeHandler = new LifetimeHandlerInterceptor(Connection, this);

            await GetInboundControlStream();
        }

        internal async ValueTask<Http3RequestStream> InitializeConnectionAndStreamsAsync(RequestDelegate application)
        {
            await InitializeConnectionAsync(application);

            OutboundControlStream = await CreateControlStream();

            return await CreateRequestStream();
        }

        private class LifetimeHandlerInterceptor : IHttp3StreamLifetimeHandler
        {
            private readonly IHttp3StreamLifetimeHandler _inner;
            private readonly Http3TestBase _http3TestBase;

            public LifetimeHandlerInterceptor(IHttp3StreamLifetimeHandler inner, Http3TestBase http3TestBase)
            {
                _inner = inner;
                _http3TestBase = http3TestBase;
            }

            public bool OnInboundControlStream(Internal.Http3.Http3ControlStream stream)
            {
                return _inner.OnInboundControlStream(stream);
            }

            public void OnInboundControlStreamSetting(Internal.Http3.Http3SettingType type, long value)
            {
                _inner.OnInboundControlStreamSetting(type, value);
            }

            public bool OnInboundDecoderStream(Internal.Http3.Http3ControlStream stream)
            {
                return _inner.OnInboundDecoderStream(stream);
            }

            public bool OnInboundEncoderStream(Internal.Http3.Http3ControlStream stream)
            {
                return _inner.OnInboundEncoderStream(stream);
            }

            public void OnStreamCompleted(IHttp3Stream stream)
            {
                _inner.OnStreamCompleted(stream);
            }

            public void OnStreamConnectionError(Http3ConnectionErrorException ex)
            {
                _inner.OnStreamConnectionError(ex);
            }

            public void OnStreamCreated(IHttp3Stream stream)
            {
                _inner.OnStreamCreated(stream);

                if (_http3TestBase._runningStreams.TryGetValue(stream.StreamId, out var testStream))
                {
                    testStream._onStreamCreatedTcs.TrySetResult();
                }
            }

            public void OnStreamHeaderReceived(IHttp3Stream stream)
            {
                _inner.OnStreamHeaderReceived(stream);

                if (_http3TestBase._runningStreams.TryGetValue(stream.StreamId, out var testStream))
                {
                    testStream._onHeaderReceivedTcs.TrySetResult();
                }
            }
        }

        protected void ConnectionClosed()
        {

        }

        private static PipeOptions GetInputPipeOptions(ServiceContext serviceContext, MemoryPool<byte> memoryPool, PipeScheduler writerScheduler) => new PipeOptions
        (
          pool: memoryPool,
          readerScheduler: serviceContext.Scheduler,
          writerScheduler: writerScheduler,
          pauseWriterThreshold: serviceContext.ServerOptions.Limits.MaxRequestBufferSize ?? 0,
          resumeWriterThreshold: serviceContext.ServerOptions.Limits.MaxRequestBufferSize ?? 0,
          useSynchronizationContext: false,
          minimumSegmentSize: memoryPool.GetMinimumSegmentSize()
        );

        private static PipeOptions GetOutputPipeOptions(ServiceContext serviceContext, MemoryPool<byte> memoryPool, PipeScheduler readerScheduler) => new PipeOptions
        (
            pool: memoryPool,
            readerScheduler: readerScheduler,
            writerScheduler: serviceContext.Scheduler,
            pauseWriterThreshold: GetOutputResponseBufferSize(serviceContext),
            resumeWriterThreshold: GetOutputResponseBufferSize(serviceContext),
            useSynchronizationContext: false,
            minimumSegmentSize: memoryPool.GetMinimumSegmentSize()
        );

        private static long GetOutputResponseBufferSize(ServiceContext serviceContext)
        {
            var bufferSize = serviceContext.ServerOptions.Limits.MaxResponseBufferSize;
            if (bufferSize == 0)
            {
                // 0 = no buffering so we need to configure the pipe so the writer waits on the reader directly
                return 1;
            }

            // null means that we have no back pressure
            return bufferSize ?? 0;
        }

        public ValueTask<Http3ControlStream> CreateControlStream()
        {
            return CreateControlStream(id: 0);
        }

        public async ValueTask<Http3ControlStream> CreateControlStream(int? id)
        {
            var stream = new Http3ControlStream(this, StreamInitiator.Client);
            _runningStreams[stream.StreamId] = stream;

            MultiplexedConnectionContext.ToServerAcceptQueue.Writer.TryWrite(stream.StreamContext);
            if (id != null)
            {
                await stream.WriteStreamIdAsync(id.GetValueOrDefault());
            }
            return stream;
        }

        internal ValueTask<Http3RequestStream> CreateRequestStream()
        {
            var stream = new Http3RequestStream(this, Connection);
            _runningStreams[stream.StreamId] = stream;

            MultiplexedConnectionContext.ToServerAcceptQueue.Writer.TryWrite(stream.StreamContext);
            return new ValueTask<Http3RequestStream>(stream);
        }

        public ValueTask<ConnectionContext> StartBidirectionalStreamAsync()
        {
            var stream = new Http3RequestStream(this, Connection);
            // TODO put these somewhere to be read.
            return new ValueTask<ConnectionContext>(stream.StreamContext);
        }

        public class Http3StreamBase : IProtocolErrorCodeFeature
        {
            internal TaskCompletionSource _onStreamCreatedTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            internal TaskCompletionSource _onHeaderReceivedTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

            internal DuplexPipe.DuplexPipePair _pair;
            internal Http3TestBase _testBase;
            internal Http3Connection _connection;
            public long BytesReceived { get; private set; }
            public long Error { get; set; }

            public Task OnStreamCreatedTask => _onStreamCreatedTcs.Task;
            public Task OnHeaderReceivedTask => _onHeaderReceivedTcs.Task;

            protected Task SendAsync(ReadOnlySpan<byte> span)
            {
                var writableBuffer = _pair.Application.Output;
                writableBuffer.Write(span);
                return FlushAsync(writableBuffer);
            }

            protected static async Task FlushAsync(PipeWriter writableBuffer)
            {
                await writableBuffer.FlushAsync().AsTask().DefaultTimeout();
            }

            internal async Task ReceiveEndAsync()
            {
                var result = await _pair.Application.Input.ReadAsync().AsTask().DefaultTimeout();
                Assert.True(result.IsCompleted);
            }

            internal async Task<Http3FrameWithPayload> ReceiveFrameAsync()
            {
                var frame = new Http3FrameWithPayload();

                while (true)
                {
                    var result = await _pair.Application.Input.ReadAsync().AsTask().DefaultTimeout();
                    var buffer = result.Buffer;
                    var consumed = buffer.Start;
                    var examined = buffer.Start;
                    var copyBuffer = buffer;

                    try
                    {
                        Assert.True(buffer.Length > 0);

                        if (Http3FrameReader.TryReadFrame(ref buffer, frame, out var framePayload))
                        {
                            consumed = examined = framePayload.End;
                            frame.Payload = framePayload.ToArray();
                            return frame;
                        }
                        else
                        {
                            examined = buffer.End;
                        }

                        if (result.IsCompleted)
                        {
                            throw new IOException("The reader completed without returning a frame.");
                        }
                    }
                    finally
                    {
                        BytesReceived += copyBuffer.Slice(copyBuffer.Start, consumed).Length;
                        _pair.Application.Input.AdvanceTo(consumed, examined);
                    }
                }
            }

            internal async Task SendFrameAsync(Http3RawFrame frame, Memory<byte> data, bool endStream = false)
            {
                var outputWriter = _pair.Application.Output;
                frame.Length = data.Length;
                Http3FrameWriter.WriteHeader(frame, outputWriter);
                await SendAsync(data.Span);

                if (endStream)
                {
                    await EndStreamAsync();
                }
            }

            internal Task EndStreamAsync()
            {
                return _pair.Application.Output.CompleteAsync().AsTask();
            }

            internal async Task WaitForStreamErrorAsync(Http3ErrorCode protocolError, string expectedErrorMessage)
            {
                var readResult = await _pair.Application.Input.ReadAsync().DefaultTimeout();
                _testBase.Logger.LogTrace("Input is completed");

                Assert.True(readResult.IsCompleted);
                Assert.Equal(protocolError, (Http3ErrorCode)Error);

                if (expectedErrorMessage != null)
                {
                    Assert.Contains(_testBase.LogMessages, m => m.Exception?.Message.Contains(expectedErrorMessage) ?? false);
                }
            }
        }

        internal class Http3RequestStream : Http3StreamBase, IHttpHeadersHandler
        {
            private TestStreamContext _testStreamContext;
            private long _streamId;

            internal ConnectionContext StreamContext { get; }

            public bool CanRead => true;
            public bool CanWrite => true;

            public long StreamId => _streamId;

            public bool Disposed => _testStreamContext.Disposed;

            private readonly byte[] _headerEncodingBuffer = new byte[64 * 1024];
            private QPackEncoder _qpackEncoder = new QPackEncoder();
            private QPackDecoder _qpackDecoder = new QPackDecoder(8192);
            protected readonly Dictionary<string, string> _decodedHeaders = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            public Http3RequestStream(Http3TestBase testBase, Http3Connection connection)
            {
                _testBase = testBase;
                _connection = connection;
                var inputPipeOptions = GetInputPipeOptions(_testBase._serviceContext, _testBase._memoryPool, PipeScheduler.ThreadPool);
                var outputPipeOptions = GetOutputPipeOptions(_testBase._serviceContext, _testBase._memoryPool, PipeScheduler.ThreadPool);

                _pair = DuplexPipe.CreateConnectionPair(inputPipeOptions, outputPipeOptions);
                _streamId = testBase.GetStreamId(0x00);
                _testStreamContext = new TestStreamContext(canRead: true, canWrite: true, _pair, this, _streamId);
                StreamContext = _testStreamContext;
            }

            public async Task SendHeadersAsync(IEnumerable<KeyValuePair<string, string>> headers, bool endStream = false)
            {
                var frame = new Http3RawFrame();
                frame.PrepareHeaders();
                var buffer = _headerEncodingBuffer.AsMemory();
                var done = _qpackEncoder.BeginEncode(headers, buffer.Span, out var length);
                Assert.True(done);

                await SendFrameAsync(frame, buffer.Slice(0, length), endStream);
            }

            internal async Task SendHeadersPartialAsync()
            {
                // Send HEADERS frame header without content.
                var outputWriter = _pair.Application.Output;
                var frame = new Http3RawFrame();
                frame.PrepareData();
                frame.Length = 10;
                Http3FrameWriter.WriteHeader(frame, outputWriter);
                await SendAsync(Span<byte>.Empty);
            }

            internal async Task SendDataAsync(Memory<byte> data, bool endStream = false)
            {
                var frame = new Http3RawFrame();
                frame.PrepareData();
                await SendFrameAsync(frame, data, endStream);
            }

            internal async Task<Dictionary<string, string>> ExpectHeadersAsync()
            {
                var http3WithPayload = await ReceiveFrameAsync();
                Assert.Equal(Http3FrameType.Headers, http3WithPayload.Type);

                _decodedHeaders.Clear();
                _qpackDecoder.Decode(http3WithPayload.PayloadSequence, this);
                _qpackDecoder.Reset();
                return _decodedHeaders.ToDictionary(kvp => kvp.Key, kvp => kvp.Value, _decodedHeaders.Comparer);
            }

            internal async Task<Memory<byte>> ExpectDataAsync()
            {
                var http3WithPayload = await ReceiveFrameAsync();
                return http3WithPayload.Payload;
            }

            internal async Task ExpectReceiveEndOfStream()
            {
                var result = await _pair.Application.Input.ReadAsync().AsTask().DefaultTimeout();
                Assert.True(result.IsCompleted);
            }

            public void OnHeader(ReadOnlySpan<byte> name, ReadOnlySpan<byte> value)
            {
                _decodedHeaders[name.GetAsciiStringNonNullCharacters()] = value.GetAsciiOrUTF8StringNonNullCharacters();
            }

            public void OnHeadersComplete(bool endHeaders)
            {
            }

            public void OnStaticIndexedHeader(int index)
            {
                var knownHeader = H3StaticTable.GetHeaderFieldAt(index);
                _decodedHeaders[((Span<byte>)knownHeader.Name).GetAsciiStringNonNullCharacters()] = HttpUtilities.GetAsciiOrUTF8StringNonNullCharacters((ReadOnlySpan<byte>)knownHeader.Value);
            }

            public void OnStaticIndexedHeader(int index, ReadOnlySpan<byte> value)
            {
                _decodedHeaders[((Span<byte>)H3StaticTable.GetHeaderFieldAt(index).Name).GetAsciiStringNonNullCharacters()] = value.GetAsciiOrUTF8StringNonNullCharacters();
            }
        }

        internal class Http3FrameWithPayload : Http3RawFrame
        {
            public Http3FrameWithPayload() : base()
            {
            }

            // This does not contain extended headers
            public Memory<byte> Payload { get; set; }

            public ReadOnlySequence<byte> PayloadSequence => new ReadOnlySequence<byte>(Payload);
        }

        public enum StreamInitiator
        {
            Client,
            Server
        }

        public class Http3ControlStream : Http3StreamBase
        {
            internal ConnectionContext StreamContext { get; }
            private long _streamId;

            public bool CanRead => true;
            public bool CanWrite => false;

            public long StreamId => _streamId;

            public Http3ControlStream(Http3TestBase testBase, StreamInitiator initiator)
            {
                _testBase = testBase;
                var inputPipeOptions = GetInputPipeOptions(_testBase._serviceContext, _testBase._memoryPool, PipeScheduler.ThreadPool);
                var outputPipeOptions = GetOutputPipeOptions(_testBase._serviceContext, _testBase._memoryPool, PipeScheduler.ThreadPool);
                _pair = DuplexPipe.CreateConnectionPair(inputPipeOptions, outputPipeOptions);
                _streamId = testBase.GetStreamId(initiator == StreamInitiator.Client ? 0x02 : 0x03);
                StreamContext = new TestStreamContext(canRead: true, canWrite: false, _pair, this, _streamId);
            }

            public Http3ControlStream(ConnectionContext streamContext)
            {
                StreamContext = streamContext;
            }

            internal async Task ExpectSettingsAsync()
            {
                var http3WithPayload = await ReceiveFrameAsync();
                Assert.Equal(Http3FrameType.Settings, http3WithPayload.Type);
            }

            public async Task WriteStreamIdAsync(int id)
            {
                var writableBuffer = _pair.Application.Output;

                void WriteSpan(PipeWriter pw)
                {
                    var buffer = pw.GetSpan(sizeHint: 8);
                    var lengthWritten = VariableLengthIntegerHelper.WriteInteger(buffer, id);
                    pw.Advance(lengthWritten);
                }

                WriteSpan(writableBuffer);

                await FlushAsync(writableBuffer);
            }

            internal async Task SendGoAwayAsync(long streamId, bool endStream = false)
            {
                var frame = new Http3RawFrame();
                frame.PrepareGoAway();

                var data = new byte[VariableLengthIntegerHelper.GetByteCount(streamId)];
                VariableLengthIntegerHelper.WriteInteger(data, streamId);

                await SendFrameAsync(frame, data, endStream);
            }

            internal async Task SendSettingsAsync(List<Http3PeerSetting> settings, bool endStream = false)
            {
                var frame = new Http3RawFrame();
                frame.PrepareSettings();

                var settingsLength = CalculateSettingsSize(settings);
                var buffer = new byte[settingsLength];
                WriteSettings(settings, buffer);

                await SendFrameAsync(frame, buffer, endStream);
            }

            internal static int CalculateSettingsSize(List<Http3PeerSetting> settings)
            {
                var length = 0;
                foreach (var setting in settings)
                {
                    length += VariableLengthIntegerHelper.GetByteCount((long)setting.Parameter);
                    length += VariableLengthIntegerHelper.GetByteCount(setting.Value);
                }
                return length;
            }

            internal static void WriteSettings(List<Http3PeerSetting> settings, Span<byte> destination)
            {
                foreach (var setting in settings)
                {
                    var parameterLength = VariableLengthIntegerHelper.WriteInteger(destination, (long)setting.Parameter);
                    destination = destination.Slice(parameterLength);

                    var valueLength = VariableLengthIntegerHelper.WriteInteger(destination, (long)setting.Value);
                    destination = destination.Slice(valueLength);
                }
            }

            public async ValueTask<long> TryReadStreamIdAsync()
            {
                while (true)
                {
                    var result = await _pair.Application.Input.ReadAsync();
                    var readableBuffer = result.Buffer;
                    var consumed = readableBuffer.Start;
                    var examined = readableBuffer.End;

                    try
                    {
                        if (!readableBuffer.IsEmpty)
                        {
                            var id = VariableLengthIntegerHelper.GetInteger(readableBuffer, out consumed, out examined);
                            if (id != -1)
                            {
                                return id;
                            }
                        }

                        if (result.IsCompleted)
                        {
                            return -1;
                        }
                    }
                    finally
                    {
                        _pair.Application.Input.AdvanceTo(consumed, examined);
                    }
                }
            }
        }

        public class TestMultiplexedConnectionContext : MultiplexedConnectionContext, IConnectionLifetimeNotificationFeature, IConnectionLifetimeFeature, IConnectionHeartbeatFeature, IProtocolErrorCodeFeature
        {
            public readonly Channel<ConnectionContext> ToServerAcceptQueue = Channel.CreateUnbounded<ConnectionContext>(new UnboundedChannelOptions
            {
                SingleReader = true,
                SingleWriter = true
            });

            public readonly Channel<Http3ControlStream> ToClientAcceptQueue = Channel.CreateUnbounded<Http3ControlStream>(new UnboundedChannelOptions
            {
                SingleReader = true,
                SingleWriter = true
            });

            private readonly Http3TestBase _testBase;
            private long _error;

            public TestMultiplexedConnectionContext(Http3TestBase testBase)
            {
                _testBase = testBase;
                Features = new FeatureCollection();
                Features.Set<IConnectionLifetimeNotificationFeature>(this);
                Features.Set<IConnectionHeartbeatFeature>(this);
                Features.Set<IProtocolErrorCodeFeature>(this);
                ConnectionClosedRequested = ConnectionClosingCts.Token;
            }

            public override string ConnectionId { get; set; }

            public override IFeatureCollection Features { get; }

            public override IDictionary<object, object> Items { get; set; }

            public CancellationToken ConnectionClosedRequested { get; set; }

            public CancellationTokenSource ConnectionClosingCts { get; set; } = new CancellationTokenSource();

            public long Error
            {
                get => _error;
                set => _error = value;
            }

            public override void Abort()
            {
                Abort(new ConnectionAbortedException());
            }

            public override void Abort(ConnectionAbortedException abortReason)
            {
                ToServerAcceptQueue.Writer.TryComplete();
            }

            public override async ValueTask<ConnectionContext> AcceptAsync(CancellationToken cancellationToken = default)
            {
                while (await ToServerAcceptQueue.Reader.WaitToReadAsync())
                {
                    while (ToServerAcceptQueue.Reader.TryRead(out var connection))
                    {
                        return connection;
                    }
                }

                return null;
            }

            public override ValueTask<ConnectionContext> ConnectAsync(IFeatureCollection features = null, CancellationToken cancellationToken = default)
            {
                var stream = new Http3ControlStream(_testBase, StreamInitiator.Server);
                ToClientAcceptQueue.Writer.WriteAsync(stream);
                return new ValueTask<ConnectionContext>(stream.StreamContext);
            }

            public void OnHeartbeat(Action<object> action, object state)
            {
            }

            public void RequestClose()
            {
                throw new NotImplementedException();
            }
        }

        private class TestStreamContext : ConnectionContext, IStreamDirectionFeature, IStreamIdFeature
        {
            private DuplexPipePair _pair;
            public TestStreamContext(bool canRead, bool canWrite, DuplexPipePair pair, IProtocolErrorCodeFeature errorCodeFeature, long streamId)
            {
                _pair = pair;
                Features = new FeatureCollection();
                Features.Set<IStreamDirectionFeature>(this);
                Features.Set<IStreamIdFeature>(this);
                Features.Set(errorCodeFeature);

                CanRead = canRead;
                CanWrite = canWrite;
                StreamId = streamId;
            }

            public bool Disposed { get; private set; }

            public override string ConnectionId { get; set; }

            public long StreamId { get; }

            public override IFeatureCollection Features { get; }

            public override IDictionary<object, object> Items { get; set; }

            public override IDuplexPipe Transport
            {
                get
                {
                    return _pair.Transport;
                }
                set
                {
                    throw new NotImplementedException();
                }
            }

            public bool CanRead { get; }

            public bool CanWrite { get; }

            public override void Abort(ConnectionAbortedException abortReason)
            {
                _pair.Application.Output.Complete(abortReason);
            }

            public override ValueTask DisposeAsync()
            {
                Disposed = true;
                return base.DisposeAsync();
            }
        }
    }
}
