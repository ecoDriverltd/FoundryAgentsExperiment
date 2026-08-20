using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using System.Threading;
using System.Threading.Tasks;
using System.Threading.Tasks.Sources;
using AGUI.Abstractions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace AGUI.Client;

/// <summary>
/// Provides an <see cref="T:Microsoft.Extensions.AI.IChatClient" /> implementation for AG-UI protocol.
/// </summary>
public sealed class AGUIChatClient : DelegatingChatClient
{
	private sealed class AGUIChatClientHandler : IChatClient, IDisposable
	{
		[CompilerGenerated]
		private sealed class <GetStreamingResponseAsync>d__7 : IAsyncEnumerable<ChatResponseUpdate>, IAsyncEnumerator<ChatResponseUpdate>, IAsyncDisposable, IValueTaskSource<bool>, IValueTaskSource, IAsyncStateMachine
		{
			public int <>1__state;

			public AsyncIteratorMethodBuilder <>t__builder;

			public ManualResetValueTaskSourceCore<bool> <>v__promiseOfValueOrEnd;

			private ChatResponseUpdate <>2__current;

			private bool <>w__disposeMode;

			private CancellationTokenSource <>x__combinedTokens;

			private int <>l__initialThreadId;

			private IEnumerable<ChatMessage> messages;

			public IEnumerable<ChatMessage> <>3__messages;

			private ChatOptions options;

			public ChatOptions <>3__options;

			public AGUIChatClientHandler <>4__this;

			private CancellationToken cancellationToken;

			public CancellationToken <>3__cancellationToken;

			private string <threadId>5__2;

			private HashSet<string> <clientToolSet>5__3;

			private ConfiguredCancelableAsyncEnumerable<ChatResponseUpdate>.Enumerator <>7__wrap3;

			private object <>7__wrap4;

			private int <>7__wrap5;

			private ConfiguredValueTaskAwaitable<bool>.ConfiguredValueTaskAwaiter <>u__1;

			private ConfiguredValueTaskAwaitable.ConfiguredValueTaskAwaiter <>u__2;

			ChatResponseUpdate IAsyncEnumerator<ChatResponseUpdate>.Current
			{
				[DebuggerHidden]
				get
				{
					return <>2__current;
				}
			}

			[DebuggerHidden]
			public <GetStreamingResponseAsync>d__7(int <>1__state)
			{
				<>t__builder = AsyncIteratorMethodBuilder.Create();
				this.<>1__state = <>1__state;
				<>l__initialThreadId = Environment.CurrentManagedThreadId;
			}

			private void MoveNext()
			{
				//IL_01c0: Unknown result type (might be due to invalid IL or missing references)
				//IL_01c5: Unknown result type (might be due to invalid IL or missing references)
				//IL_01e1: Expected O, but got Unknown
				//IL_01e6: Expected O, but got Unknown
				//IL_0231: Unknown result type (might be due to invalid IL or missing references)
				//IL_0236: Unknown result type (might be due to invalid IL or missing references)
				//IL_0239: Expected O, but got Unknown
				//IL_023e: Expected O, but got Unknown
				int num = <>1__state;
				AGUIChatClientHandler aGUIChatClientHandler = <>4__this;
				try
				{
					ConfiguredValueTaskAwaitable.ConfiguredValueTaskAwaiter awaiter;
					switch (num)
					{
					default:
						if (!<>w__disposeMode)
						{
							num = (<>1__state = -1);
							List<ChatMessage> messagesList = messages.ToList();
							ChatOptions obj = options;
							object obj2 = ((obj == null) ? null : obj.RawRepresentationFactory?.Invoke((IChatClient)(object)aGUIChatClientHandler));
							RunAgentInput val = (RunAgentInput)((obj2 is RunAgentInput) ? obj2 : null);
							<threadId>5__2 = (string.IsNullOrEmpty((val != null) ? val.ThreadId : null) ? null : val.ThreadId) ?? ExtractTemporaryThreadId(messagesList) ?? ExtractThreadIdFromOptions(options) ?? AGUIIdGenerator.NewThreadId();
							RunAgentInput input = BuildRunAgentInput(messagesList, options, val, <threadId>5__2, aGUIChatClientHandler._jsonSerializerOptions);
							<clientToolSet>5__3 = new HashSet<string>();
							ChatOptions obj3 = options;
							IEnumerator<AITool> enumerator = (((obj3 != null) ? obj3.Tools : null) ?? new List<AITool>()).GetEnumerator();
							try
							{
								while (enumerator.MoveNext())
								{
									AITool current = enumerator.Current;
									<clientToolSet>5__3.Add(current.Name);
								}
							}
							finally
							{
								if (num == -1)
								{
									enumerator?.Dispose();
								}
							}
							if (!<>w__disposeMode)
							{
								<>7__wrap3 = EventStreamConverter.AsChatResponseUpdates(aGUIChatClientHandler._transport.SendAsync(input, cancellationToken), aGUIChatClientHandler._jsonSerializerOptions, cancellationToken).ConfigureAwait(continueOnCapturedContext: false).GetAsyncEnumerator();
								<>7__wrap4 = null;
								<>7__wrap5 = 0;
								goto case -4;
							}
						}
						goto end_IL_000e;
					case -4:
					case 0:
						try
						{
							ConfiguredValueTaskAwaitable<bool>.ConfiguredValueTaskAwaiter awaiter2;
							if (num != -4)
							{
								if (num != 0)
								{
									goto IL_02d3;
								}
								awaiter2 = <>u__1;
								<>u__1 = default(ConfiguredValueTaskAwaitable<bool>.ConfiguredValueTaskAwaiter);
								num = (<>1__state = -1);
								goto IL_033e;
							}
							num = (<>1__state = -1);
							if (!<>w__disposeMode)
							{
								goto IL_02d3;
							}
							goto end_IL_0190;
							IL_02d3:
							<>2__current = null;
							awaiter2 = <>7__wrap3.MoveNextAsync().GetAwaiter();
							if (!awaiter2.IsCompleted)
							{
								num = (<>1__state = 0);
								<>u__1 = awaiter2;
								<GetStreamingResponseAsync>d__7 stateMachine = this;
								<>t__builder.AwaitUnsafeOnCompleted(ref awaiter2, ref stateMachine);
								return;
							}
							goto IL_033e;
							IL_033e:
							if (awaiter2.GetResult())
							{
								ChatResponseUpdate current2 = <>7__wrap3.Current;
								if (current2.RawRepresentation is RunStartedEvent)
								{
									AdditionalPropertiesDictionary val2 = new AdditionalPropertiesDictionary();
									((AdditionalPropertiesDictionary<object>)val2)["agui_thread_id"] = current2.ConversationId ?? <threadId>5__2;
									current2.AdditionalProperties = val2;
								}
								FunctionCallContent val3 = current2.Contents.OfType<FunctionCallContent>().FirstOrDefault();
								if (val3 != null)
								{
									if (<clientToolSet>5__3.Count > 0 && <clientToolSet>5__3.Contains(val3.Name))
									{
										FunctionCallContent val4 = val3;
										if (((AIContent)val4).AdditionalProperties == null)
										{
											AdditionalPropertiesDictionary val5 = new AdditionalPropertiesDictionary();
											AdditionalPropertiesDictionary val6 = val5;
											((AIContent)val4).AdditionalProperties = val5;
										}
										((AdditionalPropertiesDictionary<object>)(object)((AIContent)val3).AdditionalProperties)["agui_thread_id"] = current2.ConversationId ?? <threadId>5__2;
									}
									else
									{
										for (int i = 0; i < current2.Contents.Count; i++)
										{
											AIContent obj4 = current2.Contents[i];
											FunctionCallContent val7 = (FunctionCallContent)(object)((obj4 is FunctionCallContent) ? obj4 : null);
											if (val7 != null)
											{
												val7.InformationalOnly = true;
											}
										}
									}
								}
								current2.ConversationId = null;
								<>2__current = current2;
								num = (<>1__state = -4);
								goto IL_04db;
							}
							end_IL_0190:;
						}
						catch (object obj5)
						{
							<>7__wrap4 = obj5;
						}
						<>2__current = null;
						awaiter = <>7__wrap3.DisposeAsync().GetAwaiter();
						if (!awaiter.IsCompleted)
						{
							num = (<>1__state = 1);
							<>u__2 = awaiter;
							<GetStreamingResponseAsync>d__7 stateMachine = this;
							<>t__builder.AwaitUnsafeOnCompleted(ref awaiter, ref stateMachine);
							return;
						}
						break;
					case 1:
						awaiter = <>u__2;
						<>u__2 = default(ConfiguredValueTaskAwaitable.ConfiguredValueTaskAwaiter);
						num = (<>1__state = -1);
						break;
					}
					awaiter.GetResult();
					object obj6 = <>7__wrap4;
					if (obj6 != null)
					{
						ExceptionDispatchInfo.Capture((obj6 as Exception) ?? throw obj6).Throw();
					}
					_ = <>7__wrap5;
					if (!<>w__disposeMode)
					{
						<>7__wrap4 = null;
						<>7__wrap3 = default(ConfiguredCancelableAsyncEnumerable<ChatResponseUpdate>.Enumerator);
					}
					end_IL_000e:;
				}
				catch (Exception exception)
				{
					<>1__state = -2;
					<threadId>5__2 = null;
					<clientToolSet>5__3 = null;
					<>7__wrap3 = default(ConfiguredCancelableAsyncEnumerable<ChatResponseUpdate>.Enumerator);
					<>7__wrap4 = null;
					if (<>x__combinedTokens != null)
					{
						<>x__combinedTokens.Dispose();
						<>x__combinedTokens = null;
					}
					<>2__current = null;
					<>t__builder.Complete();
					<>v__promiseOfValueOrEnd.SetException(exception);
					return;
				}
				<>1__state = -2;
				<threadId>5__2 = null;
				<clientToolSet>5__3 = null;
				<>7__wrap3 = default(ConfiguredCancelableAsyncEnumerable<ChatResponseUpdate>.Enumerator);
				<>7__wrap4 = null;
				if (<>x__combinedTokens != null)
				{
					<>x__combinedTokens.Dispose();
					<>x__combinedTokens = null;
				}
				<>2__current = null;
				<>t__builder.Complete();
				<>v__promiseOfValueOrEnd.SetResult(result: false);
				return;
				IL_04db:
				<>v__promiseOfValueOrEnd.SetResult(result: true);
			}

			void IAsyncStateMachine.MoveNext()
			{
				//ILSpy generated this explicit interface implementation from .override directive in MoveNext
				this.MoveNext();
			}

			[DebuggerHidden]
			private void SetStateMachine(IAsyncStateMachine stateMachine)
			{
			}

			void IAsyncStateMachine.SetStateMachine(IAsyncStateMachine stateMachine)
			{
				//ILSpy generated this explicit interface implementation from .override directive in SetStateMachine
				this.SetStateMachine(stateMachine);
			}

			[DebuggerHidden]
			IAsyncEnumerator<ChatResponseUpdate> IAsyncEnumerable<ChatResponseUpdate>.GetAsyncEnumerator(CancellationToken cancellationToken = default(CancellationToken))
			{
				<GetStreamingResponseAsync>d__7 <GetStreamingResponseAsync>d__8;
				if (<>1__state == -2 && <>l__initialThreadId == Environment.CurrentManagedThreadId)
				{
					<>1__state = -3;
					<>t__builder = AsyncIteratorMethodBuilder.Create();
					<>w__disposeMode = false;
					<GetStreamingResponseAsync>d__8 = this;
				}
				else
				{
					<GetStreamingResponseAsync>d__8 = new <GetStreamingResponseAsync>d__7(-3)
					{
						<>4__this = <>4__this
					};
				}
				<GetStreamingResponseAsync>d__8.messages = <>3__messages;
				<GetStreamingResponseAsync>d__8.options = <>3__options;
				if (<>3__cancellationToken.Equals(default(CancellationToken)))
				{
					<GetStreamingResponseAsync>d__8.cancellationToken = cancellationToken;
				}
				else if (cancellationToken.Equals(<>3__cancellationToken) || cancellationToken.Equals(default(CancellationToken)))
				{
					<GetStreamingResponseAsync>d__8.cancellationToken = <>3__cancellationToken;
				}
				else
				{
					<>x__combinedTokens = CancellationTokenSource.CreateLinkedTokenSource(<>3__cancellationToken, cancellationToken);
					<GetStreamingResponseAsync>d__8.cancellationToken = <>x__combinedTokens.Token;
				}
				return <GetStreamingResponseAsync>d__8;
			}

			[DebuggerHidden]
			ValueTask<bool> IAsyncEnumerator<ChatResponseUpdate>.MoveNextAsync()
			{
				if (<>1__state == -2)
				{
					return default(ValueTask<bool>);
				}
				<>v__promiseOfValueOrEnd.Reset();
				<GetStreamingResponseAsync>d__7 stateMachine = this;
				<>t__builder.MoveNext(ref stateMachine);
				short version = <>v__promiseOfValueOrEnd.Version;
				if (<>v__promiseOfValueOrEnd.GetStatus(version) == ValueTaskSourceStatus.Succeeded)
				{
					return new ValueTask<bool>(<>v__promiseOfValueOrEnd.GetResult(version));
				}
				return new ValueTask<bool>(this, version);
			}

			[DebuggerHidden]
			bool IValueTaskSource<bool>.GetResult(short token)
			{
				return <>v__promiseOfValueOrEnd.GetResult(token);
			}

			[DebuggerHidden]
			ValueTaskSourceStatus IValueTaskSource<bool>.GetStatus(short token)
			{
				return <>v__promiseOfValueOrEnd.GetStatus(token);
			}

			[DebuggerHidden]
			void IValueTaskSource<bool>.OnCompleted(Action<object?> continuation, object? state, short token, ValueTaskSourceOnCompletedFlags flags)
			{
				<>v__promiseOfValueOrEnd.OnCompleted(continuation, state, token, flags);
			}

			[DebuggerHidden]
			void IValueTaskSource.GetResult(short token)
			{
				<>v__promiseOfValueOrEnd.GetResult(token);
			}

			[DebuggerHidden]
			ValueTaskSourceStatus IValueTaskSource.GetStatus(short token)
			{
				return <>v__promiseOfValueOrEnd.GetStatus(token);
			}

			[DebuggerHidden]
			void IValueTaskSource.OnCompleted(Action<object?> continuation, object? state, short token, ValueTaskSourceOnCompletedFlags flags)
			{
				<>v__promiseOfValueOrEnd.OnCompleted(continuation, state, token, flags);
			}

			[DebuggerHidden]
			ValueTask IAsyncDisposable.DisposeAsync()
			{
				if (<>1__state >= -1)
				{
					throw new NotSupportedException();
				}
				if (<>1__state == -2)
				{
					return default(ValueTask);
				}
				<>w__disposeMode = true;
				<>v__promiseOfValueOrEnd.Reset();
				<GetStreamingResponseAsync>d__7 stateMachine = this;
				<>t__builder.MoveNext(ref stateMachine);
				return new ValueTask(this, <>v__promiseOfValueOrEnd.Version);
			}
		}

		private readonly IAGUITransport _transport;

		private readonly JsonSerializerOptions _jsonSerializerOptions;

		public ChatClientMetadata Metadata { get; }

		public AGUIChatClientHandler(IAGUITransport transport, JsonSerializerOptions jsonSerializerOptions)
		{
			//IL_001c: Unknown result type (might be due to invalid IL or missing references)
			//IL_0026: Expected O, but got Unknown
			_transport = transport;
			_jsonSerializerOptions = jsonSerializerOptions;
			Metadata = new ChatClientMetadata("ag-ui", (Uri)null, (string)null);
		}

		public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default(CancellationToken))
		{
			return ChatResponseExtensions.ToChatResponseAsync(GetStreamingResponseAsync(messages, options, cancellationToken), cancellationToken);
		}

		[AsyncIteratorStateMachine(typeof(<GetStreamingResponseAsync>d__7))]
		public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default(CancellationToken))
		{
			return new <GetStreamingResponseAsync>d__7(-2)
			{
				<>4__this = this,
				<>3__messages = messages,
				<>3__options = options,
				<>3__cancellationToken = cancellationToken
			};
		}

		public void Dispose()
		{
		}

		public object? GetService(Type serviceType, object? serviceKey = null)
		{
			if (serviceType == typeof(ChatClientMetadata))
			{
				return Metadata;
			}
			if (serviceType == typeof(ActivitySource))
			{
				return AGUIClientInstrumentation.ActivitySource;
			}
			return null;
		}

		private static RunAgentInput BuildRunAgentInput(List<ChatMessage> messagesList, ChatOptions? options, RunAgentInput? providedInput, string threadId, JsonSerializerOptions jsonSerializerOptions)
		{
			//IL_0000: Unknown result type (might be due to invalid IL or missing references)
			//IL_0005: Unknown result type (might be due to invalid IL or missing references)
			//IL_000c: Unknown result type (might be due to invalid IL or missing references)
			//IL_0032: Unknown result type (might be due to invalid IL or missing references)
			//IL_0044: Expected O, but got Unknown
			//IL_0280: Unknown result type (might be due to invalid IL or missing references)
			//IL_0285: Unknown result type (might be due to invalid IL or missing references)
			//IL_0292: Unknown result type (might be due to invalid IL or missing references)
			//IL_029f: Unknown result type (might be due to invalid IL or missing references)
			//IL_02ae: Expected O, but got Unknown
			//IL_03d4: Unknown result type (might be due to invalid IL or missing references)
			//IL_03d9: Unknown result type (might be due to invalid IL or missing references)
			//IL_03e6: Unknown result type (might be due to invalid IL or missing references)
			//IL_03f1: Unknown result type (might be due to invalid IL or missing references)
			//IL_0403: Expected O, but got Unknown
			//IL_02f8: Unknown result type (might be due to invalid IL or missing references)
			//IL_02fd: Unknown result type (might be due to invalid IL or missing references)
			//IL_030a: Unknown result type (might be due to invalid IL or missing references)
			//IL_0315: Unknown result type (might be due to invalid IL or missing references)
			//IL_0316: Unknown result type (might be due to invalid IL or missing references)
			//IL_031b: Unknown result type (might be due to invalid IL or missing references)
			//IL_0328: Unknown result type (might be due to invalid IL or missing references)
			//IL_0330: Unknown result type (might be due to invalid IL or missing references)
			//IL_034e: Expected O, but got Unknown
			//IL_035d: Expected O, but got Unknown
			RunAgentInput val = new RunAgentInput
			{
				ThreadId = threadId,
				RunId = (string.IsNullOrEmpty((providedInput != null) ? providedInput.RunId : null) ? AGUIIdGenerator.NewRunId() : providedInput.RunId),
				Messages = AGUIChatMessageExtensions.AsAGUIMessages((IEnumerable<ChatMessage>)messagesList).ToList()
			};
			bool flag = false;
			IList<AGUITool> tools;
			if (providedInput != null)
			{
				IList<AGUIMessage> messages = providedInput.Messages;
				if (messages != null && messages.Count > 0)
				{
					val.Messages = providedInput.Messages;
				}
				tools = providedInput.Tools;
				if (tools != null && tools.Count > 0)
				{
					val.Tools = providedInput.Tools;
				}
				if (providedInput.State.HasValue)
				{
					val.State = providedInput.State;
				}
				if (!string.IsNullOrEmpty(providedInput.ParentRunId))
				{
					val.ParentRunId = providedInput.ParentRunId;
				}
				IList<AGUIContext> context = providedInput.Context;
				if (context != null && context.Count > 0)
				{
					val.Context = providedInput.Context;
				}
				if (providedInput.ForwardedProperties.ValueKind != JsonValueKind.Undefined)
				{
					val.ForwardedProperties = providedInput.ForwardedProperties;
				}
				IList<AGUIResume> resume = providedInput.Resume;
				if (resume != null && resume.Count > 0)
				{
					val.Resume = providedInput.Resume;
					flag = true;
				}
			}
			tools = val.Tools;
			if (tools == null || tools.Count <= 0)
			{
				IList<AITool> list = ((options != null) ? options.Tools : null);
				if (list != null && list.Count > 0)
				{
					val.Tools = AGUIToolExtensions.AsAGUITools((IEnumerable<AITool>)options.Tools).ToList();
				}
			}
			List<ToolApprovalResponseContent> list2 = null;
			List<InterruptResponseContent> list3 = null;
			if (options != null)
			{
				((AdditionalPropertiesDictionary<object>)(object)options.AdditionalProperties)?.TryGetValue<List<ToolApprovalResponseContent>>("agui_approval_responses", ref list2);
			}
			if (options != null)
			{
				((AdditionalPropertiesDictionary<object>)(object)options.AdditionalProperties)?.TryGetValue<List<InterruptResponseContent>>("agui_interrupt_responses", ref list3);
			}
			if (flag)
			{
				int num = list2?.Count ?? 0;
				int num2 = list3?.Count ?? 0;
				if (num > 0 || num2 > 0)
				{
					Activity? current = Activity.Current;
					if (current != null)
					{
						ActivityTagsCollection tags = new ActivityTagsCollection
						{
							{ "agui.resume.dropped_approval_responses", num },
							{ "agui.resume.dropped_interrupt_responses", num2 }
						};
						current.AddEvent(new ActivityEvent("agui.resume.caller_override_dropped_responses", default(DateTimeOffset), tags));
					}
				}
			}
			else
			{
				if (list2 != null && list2.Count > 0)
				{
					List<AGUIResume> list4 = new List<AGUIResume>(list2.Count);
					object obj = default(object);
					foreach (ToolApprovalResponseContent item in list2)
					{
						AGUIToolCallInfo toolCall = null;
						ToolCallContent toolCall2 = item.ToolCall;
						FunctionCallContent val2 = (FunctionCallContent)(object)((toolCall2 is FunctionCallContent) ? toolCall2 : null);
						if (val2 != null)
						{
							toolCall = new AGUIToolCallInfo
							{
								CallId = ((ToolCallContent)val2).CallId,
								Name = val2.Name,
								Arguments = val2.Arguments
							};
						}
						string result = null;
						if (((AdditionalPropertiesDictionary<object>)(object)((AIContent)item).AdditionalProperties)?.TryGetValue("result", ref obj) ?? false)
						{
							result = obj as string;
						}
						list4.Add(new AGUIResume
						{
							InterruptId = ((InputResponseContent)item).RequestId,
							Status = "resolved",
							Payload = JsonSerializer.SerializeToElement((object?)new AGUIToolApprovalResumePayload
							{
								Approved = item.Approved,
								ToolCall = toolCall,
								Result = result
							}, jsonSerializerOptions.GetTypeInfo(typeof(AGUIToolApprovalResumePayload)))
						});
					}
					val.Resume = list4;
				}
				if (list3 != null && list3.Count > 0)
				{
					IList<AGUIResume> resume2 = val.Resume;
					List<AGUIResume> list5 = ((resume2 != null && resume2.Count > 0) ? new List<AGUIResume>(resume2) : new List<AGUIResume>(list3.Count));
					foreach (InterruptResponseContent item2 in list3)
					{
						list5.Add(new AGUIResume
						{
							InterruptId = ((InputResponseContent)item2).RequestId,
							Status = "resolved",
							Payload = item2.Payload
						});
					}
					val.Resume = list5;
				}
			}
			return val;
		}

		private static string? ExtractThreadIdFromOptions(ChatOptions? options)
		{
			string text = default(string);
			if (((options != null) ? options.AdditionalProperties : null) == null || !((AdditionalPropertiesDictionary<object>)(object)options.AdditionalProperties).TryGetValue<string>("agui_thread_id", ref text) || string.IsNullOrEmpty(text))
			{
				return null;
			}
			return text;
		}

		private static string? ExtractTemporaryThreadId(List<ChatMessage> messagesList)
		{
			if (messagesList.Count < 2)
			{
				return null;
			}
			ChatMessage val = messagesList[messagesList.Count - 2];
			if (val.Contents.Count >= 1)
			{
				AIContent obj = val.Contents[0];
				FunctionCallContent val2 = (FunctionCallContent)(object)((obj is FunctionCallContent) ? obj : null);
				if (val2 != null)
				{
					string text = default(string);
					if (((AIContent)val2).AdditionalProperties == null || !((AdditionalPropertiesDictionary<object>)(object)((AIContent)val2).AdditionalProperties).TryGetValue<string>("agui_thread_id", ref text) || string.IsNullOrEmpty(text))
					{
						return null;
					}
					return text;
				}
			}
			return null;
		}
	}

	[CompilerGenerated]
	private sealed class <GetStreamingResponseAsync>d__2 : IAsyncEnumerable<ChatResponseUpdate>, IAsyncEnumerator<ChatResponseUpdate>, IAsyncDisposable, IValueTaskSource<bool>, IValueTaskSource, IAsyncStateMachine
	{
		public int <>1__state;

		public AsyncIteratorMethodBuilder <>t__builder;

		public ManualResetValueTaskSourceCore<bool> <>v__promiseOfValueOrEnd;

		private ChatResponseUpdate <>2__current;

		private bool <>w__disposeMode;

		private CancellationTokenSource <>x__combinedTokens;

		private int <>l__initialThreadId;

		private ChatOptions options;

		public ChatOptions <>3__options;

		private IEnumerable<ChatMessage> messages;

		public IEnumerable<ChatMessage> <>3__messages;

		public AGUIChatClient <>4__this;

		private CancellationToken cancellationToken;

		public CancellationToken <>3__cancellationToken;

		private bool <threadIdPinned>5__2;

		private ConfiguredCancelableAsyncEnumerable<ChatResponseUpdate>.Enumerator <>7__wrap2;

		private object <>7__wrap3;

		private int <>7__wrap4;

		private ConfiguredValueTaskAwaitable<bool>.ConfiguredValueTaskAwaiter <>u__1;

		private ConfiguredValueTaskAwaitable.ConfiguredValueTaskAwaiter <>u__2;

		ChatResponseUpdate IAsyncEnumerator<ChatResponseUpdate>.Current
		{
			[DebuggerHidden]
			get
			{
				return <>2__current;
			}
		}

		[DebuggerHidden]
		public <GetStreamingResponseAsync>d__2(int <>1__state)
		{
			<>t__builder = AsyncIteratorMethodBuilder.Create();
			this.<>1__state = <>1__state;
			<>l__initialThreadId = Environment.CurrentManagedThreadId;
		}

		private void MoveNext()
		{
			//IL_0081: Unknown result type (might be due to invalid IL or missing references)
			//IL_0086: Unknown result type (might be due to invalid IL or missing references)
			//IL_0089: Expected O, but got Unknown
			//IL_008e: Expected O, but got Unknown
			//IL_03f9: Unknown result type (might be due to invalid IL or missing references)
			//IL_03fe: Unknown result type (might be due to invalid IL or missing references)
			//IL_0401: Expected O, but got Unknown
			//IL_0406: Expected O, but got Unknown
			//IL_0216: Unknown result type (might be due to invalid IL or missing references)
			//IL_02f5: Unknown result type (might be due to invalid IL or missing references)
			//IL_022a: Unknown result type (might be due to invalid IL or missing references)
			//IL_022f: Unknown result type (might be due to invalid IL or missing references)
			//IL_0232: Expected O, but got Unknown
			//IL_0237: Expected O, but got Unknown
			//IL_0309: Unknown result type (might be due to invalid IL or missing references)
			//IL_030e: Unknown result type (might be due to invalid IL or missing references)
			//IL_0311: Expected O, but got Unknown
			//IL_0316: Expected O, but got Unknown
			int num = <>1__state;
			AGUIChatClient aGUIChatClient = <>4__this;
			try
			{
				ChatOptions val;
				List<ChatMessage> list;
				List<ToolApprovalResponseContent> list2;
				List<InterruptResponseContent> list3;
				ConfiguredValueTaskAwaitable.ConfiguredValueTaskAwaiter awaiter;
				List<ChatMessage> list4;
				List<ChatMessage> list5;
				List<ChatMessage>.Enumerator enumerator2;
				switch (num)
				{
				default:
					if (!<>w__disposeMode)
					{
						num = (<>1__state = -1);
						<threadIdPinned>5__2 = false;
						val = options;
						ChatOptions obj = options;
						if (((obj != null) ? obj.ConversationId : null) != null)
						{
							val = options.Clone();
							ChatOptions val2 = val;
							if (val2.AdditionalProperties == null)
							{
								ChatOptions obj2 = val2;
								AdditionalPropertiesDictionary val3 = new AdditionalPropertiesDictionary();
								AdditionalPropertiesDictionary val4 = val3;
								obj2.AdditionalProperties = val3;
							}
							((AdditionalPropertiesDictionary<object>)(object)val.AdditionalProperties)["agui_thread_id"] = options.ConversationId;
							val.ConversationId = null;
						}
						list = messages.ToList();
						list2 = null;
						list3 = null;
						ChatMessage val5 = ((list.Count > 0) ? list[list.Count - 1] : null);
						if (val5 == null)
						{
							goto IL_016a;
						}
						IEnumerator<AIContent> enumerator = val5.Contents.GetEnumerator();
						try
						{
							while (enumerator.MoveNext())
							{
								AIContent current = enumerator.Current;
								ToolApprovalResponseContent val6 = (ToolApprovalResponseContent)(object)((current is ToolApprovalResponseContent) ? current : null);
								if (val6 != null)
								{
									if (list2 == null)
									{
										list2 = new List<ToolApprovalResponseContent>();
									}
									list2.Add(val6);
									continue;
								}
								InterruptResponseContent val7 = (InterruptResponseContent)(object)((current is InterruptResponseContent) ? current : null);
								if (val7 != null)
								{
									if (list3 == null)
									{
										list3 = new List<InterruptResponseContent>();
									}
									list3.Add(val7);
								}
							}
						}
						finally
						{
							if (num == -1)
							{
								enumerator?.Dispose();
							}
						}
						if (!<>w__disposeMode)
						{
							goto IL_016a;
						}
					}
					goto end_IL_000e;
				case -4:
				case 0:
					try
					{
						ConfiguredValueTaskAwaitable<bool>.ConfiguredValueTaskAwaiter awaiter2;
						if (num != -4)
						{
							if (num != 0)
							{
								goto IL_049e;
							}
							awaiter2 = <>u__1;
							<>u__1 = default(ConfiguredValueTaskAwaitable<bool>.ConfiguredValueTaskAwaiter);
							num = (<>1__state = -1);
							goto IL_0509;
						}
						num = (<>1__state = -1);
						if (!<>w__disposeMode)
						{
							goto IL_049e;
						}
						goto end_IL_035a;
						IL_049e:
						<>2__current = null;
						awaiter2 = <>7__wrap2.MoveNextAsync().GetAwaiter();
						if (!awaiter2.IsCompleted)
						{
							num = (<>1__state = 0);
							<>u__1 = awaiter2;
							<GetStreamingResponseAsync>d__2 stateMachine = this;
							<>t__builder.AwaitUnsafeOnCompleted(ref awaiter2, ref stateMachine);
							return;
						}
						goto IL_0509;
						IL_0509:
						if (awaiter2.GetResult())
						{
							ChatResponseUpdate current2 = <>7__wrap2.Current;
							if (!<threadIdPinned>5__2)
							{
								string text = default(string);
								bool? flag = ((AdditionalPropertiesDictionary<object>)(object)current2.AdditionalProperties)?.TryGetValue<string>("agui_thread_id", ref text);
								if (flag.HasValue && flag == true && !string.IsNullOrEmpty(text))
								{
									<threadIdPinned>5__2 = true;
									if (options != null && options.ConversationId == null)
									{
										ChatOptions val2 = options;
										if (val2.AdditionalProperties == null)
										{
											ChatOptions obj3 = val2;
											AdditionalPropertiesDictionary val8 = new AdditionalPropertiesDictionary();
											AdditionalPropertiesDictionary val4 = val8;
											obj3.AdditionalProperties = val8;
										}
										((AdditionalPropertiesDictionary<object>)(object)options.AdditionalProperties)["agui_thread_id"] = text;
									}
								}
							}
							for (int i = 0; i < current2.Contents.Count; i++)
							{
								AIContent obj4 = current2.Contents[i];
								FunctionCallContent val9 = (FunctionCallContent)(object)((obj4 is FunctionCallContent) ? obj4 : null);
								if (val9 != null)
								{
									((AdditionalPropertiesDictionary<object>)(object)((AIContent)val9).AdditionalProperties)?.Remove("agui_thread_id");
								}
							}
							current2.ConversationId = null;
							<>2__current = current2;
							num = (<>1__state = -4);
							goto IL_068a;
						}
						end_IL_035a:;
					}
					catch (object obj5)
					{
						<>7__wrap3 = obj5;
					}
					<>2__current = null;
					awaiter = <>7__wrap2.DisposeAsync().GetAwaiter();
					if (!awaiter.IsCompleted)
					{
						num = (<>1__state = 1);
						<>u__2 = awaiter;
						<GetStreamingResponseAsync>d__2 stateMachine = this;
						<>t__builder.AwaitUnsafeOnCompleted(ref awaiter, ref stateMachine);
						return;
					}
					break;
				case 1:
					{
						awaiter = <>u__2;
						<>u__2 = default(ConfiguredValueTaskAwaitable.ConfiguredValueTaskAwaiter);
						num = (<>1__state = -1);
						break;
					}
					IL_0328:
					<>7__wrap2 = aGUIChatClient.<>n__0(list, val, cancellationToken).ConfigureAwait(continueOnCapturedContext: false).GetAsyncEnumerator();
					<>7__wrap3 = null;
					<>7__wrap4 = 0;
					goto case -4;
					IL_016a:
					if (list2 == null || list2.Count <= 0)
					{
						goto IL_0249;
					}
					list4 = new List<ChatMessage>();
					enumerator2 = list.GetEnumerator();
					try
					{
						while (enumerator2.MoveNext())
						{
							ChatMessage current3 = enumerator2.Current;
							if (!current3.Contents.Any((AIContent c) => c is ToolApprovalRequestContent || c is ToolApprovalResponseContent))
							{
								list4.Add(current3);
							}
						}
					}
					finally
					{
						if (num == -1)
						{
							((IDisposable)enumerator2/*cast due to constrained. prefix*/).Dispose();
						}
					}
					if (!<>w__disposeMode)
					{
						list = list4;
						ChatOptions obj6 = val ?? options;
						val = (ChatOptions)(((object)((obj6 != null) ? obj6.Clone() : null)) ?? ((object)new ChatOptions()));
						ChatOptions val2 = val;
						if (val2.AdditionalProperties == null)
						{
							ChatOptions obj7 = val2;
							AdditionalPropertiesDictionary val10 = new AdditionalPropertiesDictionary();
							AdditionalPropertiesDictionary val4 = val10;
							obj7.AdditionalProperties = val10;
						}
						((AdditionalPropertiesDictionary<object>)(object)val.AdditionalProperties)["agui_approval_responses"] = list2;
						goto IL_0249;
					}
					goto end_IL_000e;
					IL_0249:
					if (list3 == null || list3.Count <= 0)
					{
						goto IL_0328;
					}
					list5 = new List<ChatMessage>();
					enumerator2 = list.GetEnumerator();
					try
					{
						while (enumerator2.MoveNext())
						{
							ChatMessage current4 = enumerator2.Current;
							if (!current4.Contents.Any((AIContent c) => c is InterruptRequestContent || c is InterruptResponseContent))
							{
								list5.Add(current4);
							}
						}
					}
					finally
					{
						if (num == -1)
						{
							((IDisposable)enumerator2/*cast due to constrained. prefix*/).Dispose();
						}
					}
					if (!<>w__disposeMode)
					{
						list = list5;
						ChatOptions obj8 = val ?? options;
						val = (ChatOptions)(((object)((obj8 != null) ? obj8.Clone() : null)) ?? ((object)new ChatOptions()));
						ChatOptions val2 = val;
						if (val2.AdditionalProperties == null)
						{
							ChatOptions obj9 = val2;
							AdditionalPropertiesDictionary val11 = new AdditionalPropertiesDictionary();
							AdditionalPropertiesDictionary val4 = val11;
							obj9.AdditionalProperties = val11;
						}
						((AdditionalPropertiesDictionary<object>)(object)val.AdditionalProperties)["agui_interrupt_responses"] = list3;
						goto IL_0328;
					}
					goto end_IL_000e;
				}
				awaiter.GetResult();
				object obj10 = <>7__wrap3;
				if (obj10 != null)
				{
					ExceptionDispatchInfo.Capture((obj10 as Exception) ?? throw obj10).Throw();
				}
				_ = <>7__wrap4;
				if (!<>w__disposeMode)
				{
					<>7__wrap3 = null;
					<>7__wrap2 = default(ConfiguredCancelableAsyncEnumerable<ChatResponseUpdate>.Enumerator);
				}
				end_IL_000e:;
			}
			catch (Exception exception)
			{
				<>1__state = -2;
				<>7__wrap2 = default(ConfiguredCancelableAsyncEnumerable<ChatResponseUpdate>.Enumerator);
				<>7__wrap3 = null;
				if (<>x__combinedTokens != null)
				{
					<>x__combinedTokens.Dispose();
					<>x__combinedTokens = null;
				}
				<>2__current = null;
				<>t__builder.Complete();
				<>v__promiseOfValueOrEnd.SetException(exception);
				return;
			}
			<>1__state = -2;
			<>7__wrap2 = default(ConfiguredCancelableAsyncEnumerable<ChatResponseUpdate>.Enumerator);
			<>7__wrap3 = null;
			if (<>x__combinedTokens != null)
			{
				<>x__combinedTokens.Dispose();
				<>x__combinedTokens = null;
			}
			<>2__current = null;
			<>t__builder.Complete();
			<>v__promiseOfValueOrEnd.SetResult(result: false);
			return;
			IL_068a:
			<>v__promiseOfValueOrEnd.SetResult(result: true);
		}

		void IAsyncStateMachine.MoveNext()
		{
			//ILSpy generated this explicit interface implementation from .override directive in MoveNext
			this.MoveNext();
		}

		[DebuggerHidden]
		private void SetStateMachine(IAsyncStateMachine stateMachine)
		{
		}

		void IAsyncStateMachine.SetStateMachine(IAsyncStateMachine stateMachine)
		{
			//ILSpy generated this explicit interface implementation from .override directive in SetStateMachine
			this.SetStateMachine(stateMachine);
		}

		[DebuggerHidden]
		IAsyncEnumerator<ChatResponseUpdate> IAsyncEnumerable<ChatResponseUpdate>.GetAsyncEnumerator(CancellationToken cancellationToken = default(CancellationToken))
		{
			<GetStreamingResponseAsync>d__2 <GetStreamingResponseAsync>d__3;
			if (<>1__state == -2 && <>l__initialThreadId == Environment.CurrentManagedThreadId)
			{
				<>1__state = -3;
				<>t__builder = AsyncIteratorMethodBuilder.Create();
				<>w__disposeMode = false;
				<GetStreamingResponseAsync>d__3 = this;
			}
			else
			{
				<GetStreamingResponseAsync>d__3 = new <GetStreamingResponseAsync>d__2(-3)
				{
					<>4__this = <>4__this
				};
			}
			<GetStreamingResponseAsync>d__3.messages = <>3__messages;
			<GetStreamingResponseAsync>d__3.options = <>3__options;
			if (<>3__cancellationToken.Equals(default(CancellationToken)))
			{
				<GetStreamingResponseAsync>d__3.cancellationToken = cancellationToken;
			}
			else if (cancellationToken.Equals(<>3__cancellationToken) || cancellationToken.Equals(default(CancellationToken)))
			{
				<GetStreamingResponseAsync>d__3.cancellationToken = <>3__cancellationToken;
			}
			else
			{
				<>x__combinedTokens = CancellationTokenSource.CreateLinkedTokenSource(<>3__cancellationToken, cancellationToken);
				<GetStreamingResponseAsync>d__3.cancellationToken = <>x__combinedTokens.Token;
			}
			return <GetStreamingResponseAsync>d__3;
		}

		[DebuggerHidden]
		ValueTask<bool> IAsyncEnumerator<ChatResponseUpdate>.MoveNextAsync()
		{
			if (<>1__state == -2)
			{
				return default(ValueTask<bool>);
			}
			<>v__promiseOfValueOrEnd.Reset();
			<GetStreamingResponseAsync>d__2 stateMachine = this;
			<>t__builder.MoveNext(ref stateMachine);
			short version = <>v__promiseOfValueOrEnd.Version;
			if (<>v__promiseOfValueOrEnd.GetStatus(version) == ValueTaskSourceStatus.Succeeded)
			{
				return new ValueTask<bool>(<>v__promiseOfValueOrEnd.GetResult(version));
			}
			return new ValueTask<bool>(this, version);
		}

		[DebuggerHidden]
		bool IValueTaskSource<bool>.GetResult(short token)
		{
			return <>v__promiseOfValueOrEnd.GetResult(token);
		}

		[DebuggerHidden]
		ValueTaskSourceStatus IValueTaskSource<bool>.GetStatus(short token)
		{
			return <>v__promiseOfValueOrEnd.GetStatus(token);
		}

		[DebuggerHidden]
		void IValueTaskSource<bool>.OnCompleted(Action<object?> continuation, object? state, short token, ValueTaskSourceOnCompletedFlags flags)
		{
			<>v__promiseOfValueOrEnd.OnCompleted(continuation, state, token, flags);
		}

		[DebuggerHidden]
		void IValueTaskSource.GetResult(short token)
		{
			<>v__promiseOfValueOrEnd.GetResult(token);
		}

		[DebuggerHidden]
		ValueTaskSourceStatus IValueTaskSource.GetStatus(short token)
		{
			return <>v__promiseOfValueOrEnd.GetStatus(token);
		}

		[DebuggerHidden]
		void IValueTaskSource.OnCompleted(Action<object?> continuation, object? state, short token, ValueTaskSourceOnCompletedFlags flags)
		{
			<>v__promiseOfValueOrEnd.OnCompleted(continuation, state, token, flags);
		}

		[DebuggerHidden]
		ValueTask IAsyncDisposable.DisposeAsync()
		{
			if (<>1__state >= -1)
			{
				throw new NotSupportedException();
			}
			if (<>1__state == -2)
			{
				return default(ValueTask);
			}
			<>w__disposeMode = true;
			<>v__promiseOfValueOrEnd.Reset();
			<GetStreamingResponseAsync>d__2 stateMachine = this;
			<>t__builder.MoveNext(ref stateMachine);
			return new ValueTask(this, <>v__promiseOfValueOrEnd.Version);
		}
	}

	/// <summary>
	/// Initializes a new instance of the <see cref="T:AGUI.Client.AGUIChatClient" /> class.
	/// </summary>
	/// <param name="options">The options that configure the transport and serialization.</param>
	public AGUIChatClient(AGUIChatClientOptions options)
		: base((IChatClient)(object)CreateInnerClient(GetTransport(options), CombineJsonSerializerOptions(options?.JsonSerializerOptions)))
	{
	}

	/// <inheritdoc />
	public override Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default(CancellationToken))
	{
		return ChatResponseExtensions.ToChatResponseAsync(((DelegatingChatClient)this).GetStreamingResponseAsync(messages, options, cancellationToken), cancellationToken);
	}

	/// <inheritdoc />
	[AsyncIteratorStateMachine(typeof(<GetStreamingResponseAsync>d__2))]
	public override IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default(CancellationToken))
	{
		return new <GetStreamingResponseAsync>d__2(-2)
		{
			<>4__this = this,
			<>3__messages = messages,
			<>3__options = options,
			<>3__cancellationToken = cancellationToken
		};
	}

	private static IAGUITransport GetTransport(AGUIChatClientOptions options)
	{
		ArgumentNullThrowHelper.ThrowIfNull(options, "options");
		return options.Transport;
	}

	private static FunctionInvokingChatClient CreateInnerClient(IAGUITransport transport, JsonSerializerOptions jsonSerializerOptions)
	{
		//IL_0014: Unknown result type (might be due to invalid IL or missing references)
		//IL_001a: Expected O, but got Unknown
		ArgumentNullThrowHelper.ThrowIfNull(transport, "transport");
		return new FunctionInvokingChatClient((IChatClient)(object)new AGUIChatClientHandler(transport, jsonSerializerOptions), (ILoggerFactory)null, (IServiceProvider)null);
	}

	private static JsonSerializerOptions CombineJsonSerializerOptions(JsonSerializerOptions? jsonSerializerOptions)
	{
		if (jsonSerializerOptions == null)
		{
			return ((JsonSerializerContext)(object)AGUIJsonSerializerContext.Default).Options;
		}
		JsonSerializerOptions jsonSerializerOptions2 = new JsonSerializerOptions(jsonSerializerOptions);
		if (!jsonSerializerOptions2.TypeInfoResolverChain.Any((IJsonTypeInfoResolver r) => r == AGUIJsonSerializerContext.Default))
		{
			jsonSerializerOptions2.TypeInfoResolverChain.Insert(0, (IJsonTypeInfoResolver)AGUIJsonSerializerContext.Default);
		}
		return jsonSerializerOptions2;
	}

	[CompilerGenerated]
	[DebuggerHidden]
	private IAsyncEnumerable<ChatResponseUpdate> <>n__0(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default(CancellationToken))
	{
		return ((DelegatingChatClient)this).GetStreamingResponseAsync(messages, options, cancellationToken);
	}
}
