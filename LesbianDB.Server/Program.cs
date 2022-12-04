﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using CommandLine;
using System.Net;
using System.Net.WebSockets;
using System.Threading;
using System.Text;
using Newtonsoft.Json;
using System.Buffers;

namespace LesbianDB.Server
{
	public static class Program
	{
		private sealed class Options
		{
			[Option("listen", Required = true, HelpText = "The HTTP websocket prefix to listen to (e.g https://lesbiandb-eu.scalegrid.com/c160d449395b5fbe70fcd18cef59264b/)")]
			public string Listen { get; set; }
			[Option("engine", Required = true, HelpText = "The storage engine to use (yuri/leveldb/saskia)")]
			public string Engine { get; set; }
			[Option("persist-dir", Required = false, HelpText = "The directory used to store the leveldb/saskia on-disk dictionary (required for leveldb, optional for saskia, have no effect for yuri)")]
			public string PersistDir { get; set; }
			[Option("binlog", Required = false, HelpText = "The path of the binlog used for persistance/enhanced durability.")]
			public string Binlog{ get; set; }
			[Option("soft-memory-limit", Required = false, HelpText = "The soft limit to memory usage (in bytes)")]
			public long SoftMemoryLimit { get; set; }

			[Option("yurimalloc.buckets", Required = false, HelpText = "The number of YuriMalloc generation 1 buckets to create (only useful for yuri storage engine, or saskia storage engine without --persist-dir set).", Default = 65536)]
			public int YuriMallocBuckets { get; set; }
			[Option("yurimalloc.gen2buckets", Required = false, HelpText = "The number of YuriMalloc generation 2 buckets to create (only useful for yuri storage engine, or saskia storage engine without --persist-dir set, zero means YuriMalloc generation 2 disabled).", Default = 0)]
			public int YuriMallocGen2Buckets { get; set; }
			[Option("yurimalloc.gen2promotiondelay", Required = false, HelpText = "The number of seconds to defer promotion of YuriMalloc data from generation 1 to generation 2 (only useful for yuri storage engine, or saskia storage engine without --persist-dir set, and YuriMalloc generation 2 is enabled).", Default = 7200)]
			public int YuriMallocGen2PromotionDelay { get; set; }
			[Option("yuri.buckets", Required = false, HelpText = "The number of buckets to create (only used with Yuri storage engine).", Default = 65536)]
			public int YuriBuckets { get; set; }
			[Option("saskia.zram", Required = false, HelpText = "Tells the Saskia storage engine to use zram instead of YuriMalloc for swapping cold data (no effect if persist-dir is specified or yuri/leveldb storage engine is used).", Default = false)]
			public bool SaskiaZram { get; set; }
		}
		private static ArrayPool<byte> arrayPool = ArrayPool<byte>.Create();
		private static ISwapAllocator CreateYuriMalloc(Options options)
		{
			int count = options.YuriMallocBuckets;
			ISwapAllocator swapAllocator = (count < 2) ? new YuriMalloc() : ((ISwapAllocator)new SimpleShardedSwapAllocator<YuriMalloc>(count));
			count = options.YuriMallocGen2Buckets;
			if (count > 0)
			{
				return new GenerationalSwapAllocator(swapAllocator, count == 1 ? new YuriMalloc() : ((ISwapAllocator)new SimpleShardedSwapAllocator<YuriMalloc>(count)), options.YuriMallocGen2PromotionDelay);
			}
			return swapAllocator;
		}
		private static EnhancedSequentialAccessDictionary CreateCompressedAsyncDictionary()
		{
			return new EnhancedSequentialAccessDictionary();
		}
		private static async Task SmartRestoreBinlog(IAsyncDictionary asyncDictionary, Stream binlog)
		{
			long height = Convert.ToInt64(await asyncDictionary.Read("LesbianDB_reserved_binlog_height"));
			if (height > 0)
			{
				binlog.Seek(height, SeekOrigin.Begin);
			}
			await YuriDatabaseEngine.RestoreBinlog(binlog, asyncDictionary);
		}
		private static void Main(string[] args)
		{
			Console.WriteLine("LesbianDB v2.1 server\nMade by Jessie Lesbian (Discord: jessielesbian#8060)\n\nParsing arguments...");
			Options options = Parser.Default.ParseArguments<Options>(args).Value;
			
			if (options is null){
				return;
			}
			string engine = options.Engine.ToLower();
			Console.WriteLine("Registering abort blockers...");
			ManualResetEventSlim exitBlocker = new ManualResetEventSlim();
			Action exit = null;
			Console.CancelKeyPress += (object obj, ConsoleCancelEventArgs e) => {
				exitBlocker.Wait();
				if (exit is { })
				{
					exit();
				}
			};
			AppDomain.CurrentDomain.ProcessExit += (object obj, EventArgs e) =>{
				exitBlocker.Wait();
				if (exit is { })
				{
					exit();
				}
			};
			IDatabaseEngine databaseEngine;
			Stream binlog;
			{
				Console.WriteLine("Opening binlog...");
				string binlogname = options.Binlog;
				if(binlogname is null){
					binlog = null;
				} else{
					binlog = new FileStream(binlogname, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 4096, FileOptions.SequentialScan | FileOptions.Asynchronous);
					OnExit += (object obj, EventArgs eventArgs) => {
						Console.WriteLine("Closing binlog...");
						binlog.Dispose();
					};
				}				
			}
			Console.WriteLine("Creating storage engine...");
			Task load;
			Task<LevelDBEngine> getDatabaseEngine;
			ISwapAllocator yuriMalloc;
			IAsyncDictionary asyncDictionary;
			string persistdir = options.PersistDir;
			if(persistdir is { }){
				if (!persistdir.EndsWith(Path.DirectorySeparatorChar))
				{
					persistdir += Path.DirectorySeparatorChar;
				}
			}
			string lockfile;
			switch (engine){
				case "yuri":
					lockfile = null;
					getDatabaseEngine = null;
					yuriMalloc = CreateYuriMalloc(options);
					int yuriBuckets = options.YuriBuckets;
					asyncDictionary = yuriBuckets < 2 ? new SequentialAccessAsyncDictionary(yuriMalloc) : ((IAsyncDictionary)new ShardedAsyncDictionary(() => new SequentialAccessAsyncDictionary(yuriMalloc), yuriBuckets));
					if(binlog is null){
						load = null;
						databaseEngine = new YuriDatabaseEngine(asyncDictionary);
					} else{
						load = YuriDatabaseEngine.RestoreBinlog(binlog, asyncDictionary);
						databaseEngine = new YuriDatabaseEngine(asyncDictionary, binlog);
					}
					break;
				case "saskia":
					getDatabaseEngine = null;
					if (persistdir is null){
						lockfile = null;
						if(options.SaskiaZram){
							asyncDictionary = new RandomFlushingCache(CreateCompressedAsyncDictionary, options.SoftMemoryLimit, true);
						} else{
							yuriMalloc = CreateYuriMalloc(options);
							asyncDictionary = new RandomFlushingCache(() => new EnhancedSequentialAccessDictionary(new EphemeralSwapHandle(yuriMalloc)), options.SoftMemoryLimit, true);
						}
						if (binlog is null)
						{
							load = null;
						}
						else
						{
							load = YuriDatabaseEngine.RestoreBinlog(binlog, asyncDictionary);
						}
					} else{
						int count = 0;
						lockfile = persistdir + "saskia-lock";
						IDisposable disposable = null;
						try{
							disposable = new FileStream(lockfile, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.SequentialScan);
						} catch{
							Console.Error.WriteLine("Unable to lock on-disk dictionary, maybe unclean shutdown? Please delete on-disk dictionary and restart if that's the case.");
							return;
						} finally{
							if(disposable is { }){
								disposable.Dispose();
							}
						}
						persistdir += "saskia-";
						asyncDictionary = new RandomFlushingCache(() => new EnhancedSequentialAccessDictionary(new FileSwapHandle(persistdir + count++)), options.SoftMemoryLimit, false);
						if (binlog is null)
						{
							load = null;
						}
						else
						{
							load = SmartRestoreBinlog(asyncDictionary, binlog);
						}
					}
					asyncDictionary = new RandomReplacementWriteThroughCache(asyncDictionary, options.SoftMemoryLimit);
					databaseEngine = binlog is null ? new YuriDatabaseEngine(asyncDictionary) : new YuriDatabaseEngine(asyncDictionary, binlog);

					break;
				case "leveldb":
					lockfile = null;
					if (persistdir is null){
						throw new Exception("--persist-dir is mandatory for leveldb storage engine!");
					}
					if(binlog is null){
						load = null;
						LevelDBEngine levelDBEngine = new LevelDBEngine(persistdir, options.SoftMemoryLimit);
						OnExit += (object obj, EventArgs eventArgs) => {
							Console.WriteLine("Closing LevelDB on-disk dictionary...");
							levelDBEngine.Dispose();
						};
						databaseEngine = levelDBEngine;
						getDatabaseEngine = null;
					} else{
						getDatabaseEngine = LevelDBEngine.RestoreBinlog(binlog, persistdir, options.SoftMemoryLimit);
						load = null;
						databaseEngine = null;
					}
					break;

				default:
					throw new Exception("Unknown storage engine: " + engine);
			}
			Console.WriteLine("Initializing HTTP listener...");
			HttpListener httpListener = new HttpListener();
			httpListener.Prefixes.Add(options.Listen);
			AsyncReaderWriterLock exitLock = new AsyncReaderWriterLock();
			Task coreloop = null;
			int exithandled = 0;
			exit = () => {
				try{
					
				} finally{
					if (Interlocked.Exchange(ref exithandled, 1) == 1)
					{
						goto end;
					}
					Console.WriteLine("Exitting...");
					if (httpListener.IsListening)
					{
						Console.WriteLine("Stopping HTTP listener...");
						httpListener.Stop();
					}
					if (coreloop is { })
					{
						Console.WriteLine("Waiting for core loop to exit...");
						coreloop.Wait();
					}
					Console.WriteLine("Waiting for all pending queries to complete...");
					exitLock.AcquireWriterLock().Wait();
					exitflag = true;
					exitLock.ReleaseWriterLock();
					EventHandler exitEventHandler = OnExit;
					if (exitEventHandler is { })
					{
						Console.WriteLine("Closing other resources...");
						exitEventHandler(null, new EventArgs());
					}
					if (lockfile is { })
					{
						Console.WriteLine("Releasing Saskia on-disk dictionary lock...");
						File.Delete(lockfile);
					}
				end:;
				}
			};
			Console.CancelKeyPress += (object sender, ConsoleCancelEventArgs e) => {
				exit();
			};
			AppDomain.CurrentDomain.ProcessExit += (object sender, EventArgs e) => {
				exit();
			};
			Console.WriteLine("Removing abort blockers...");
			exitBlocker.Set();
			Console.WriteLine("Starting HTTP listener...");
			httpListener.Start();
			coreloop = Main3(httpListener, load, getDatabaseEngine, databaseEngine, exitLock);
			coreloop.Wait();
		}
		private static event EventHandler OnExit;

		private static async Task Main3(HttpListener httpListener, Task load, Task<LevelDBEngine> getLevelDBEngine, IDatabaseEngine databaseEngine, AsyncReaderWriterLock exitLock)
		{
			if(databaseEngine is null){
				Console.WriteLine("Loading binlog...");
				LevelDBEngine levelDBEngine = await getLevelDBEngine;
				databaseEngine = levelDBEngine;
				OnExit += (object sender, EventArgs e) =>
				{
					Console.WriteLine("Closing LevelDB on-disk dictionary...");
					levelDBEngine.Dispose();
				};
			}
			if(load is { }){
				Console.WriteLine("Loading binlog...");
				await load;
			}
			Console.WriteLine("Done!");
		start:
			HttpListenerContext httpListenerContext;
			try{
				httpListenerContext = await httpListener.GetContextAsync();
			} catch{
				return;
			}
			if(httpListenerContext is { }){
				HandleConnection(httpListenerContext, databaseEngine, exitLock);
				goto start;
			}
		}
		private static async void HandleConnection(HttpListenerContext httpListenerContext, IDatabaseEngine databaseEngine, AsyncReaderWriterLock exitLock)
		{
			HttpListenerWebSocketContext httpListenerWebSocketContext;
			try{
				httpListenerWebSocketContext = await httpListenerContext.AcceptWebSocketAsync("LesbianDB-v2.1");
			} catch{
				httpListenerContext.Response.Abort();
				return;
			}
			if(httpListenerWebSocketContext is { }){
				Encoding encoding = httpListenerContext.Request.ContentEncoding;
				WebSocket webSocket = httpListenerWebSocketContext.WebSocket;
				AsyncReaderWriterLock disconnectionLock = new AsyncReaderWriterLock();
				AsyncMutex asyncMutex = new AsyncMutex();
				byte[] buffer = null;
				try
				{
					buffer = arrayPool.Rent(65536);
				start:
					using (MemoryStream memoryStream = new MemoryStream()){
						while (true)
						{
							WebSocketReceiveResult webSocketReceiveResult = await webSocket.ReceiveAsync(buffer, default);
							if (webSocketReceiveResult.MessageType.HasFlag(WebSocketMessageType.Close))
							{
								goto disconnect;
							}
							memoryStream.Write(buffer, 0, webSocketReceiveResult.Count);
							if (webSocketReceiveResult.EndOfMessage)
							{
								await disconnectionLock.AcquireReaderLock();
								ExecuteQuery(memoryStream.GetBuffer(), (int)memoryStream.Position, encoding, disconnectionLock.ReleaseReaderLock, asyncMutex, databaseEngine, webSocket, exitLock);
								goto start;
							}
						}
					}
				} catch{
					await disconnectionLock.AcquireWriterLock();
					try
					{
						await webSocket.CloseAsync(WebSocketCloseStatus.ProtocolError, "Socket IO error", default);
					}
					catch
					{
						
					}
				}
			disconnect:
				try{
					await webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Client requested close", default);
				} catch{
					
				}
				
			}
		}
		[JsonObject(MemberSerialization.Fields)]
		private sealed class Packet
		{
			public string id;
			public IEnumerable<string> reads;
			public IReadOnlyDictionary<string, string> conditions;
			public IReadOnlyDictionary<string, string> writes;
		}
		[JsonObject(MemberSerialization.Fields)]

		private sealed class Reply
		{
			public readonly string id;
			public readonly IReadOnlyDictionary<string, string> result;

			public Reply(string id, IReadOnlyDictionary<string, string> result)
			{
				this.id = id;
				this.result = result;
			}
		}
		private static bool exitflag;
		private static async void ExecuteQuery(byte[] bytes, int count, Encoding encoding, Action releaseDisconnectionLock, AsyncMutex asyncMutex, IDatabaseEngine databaseEngine, WebSocket webSocket, AsyncReaderWriterLock exitLock)
		{
			try{
				Packet packet = JsonConvert.DeserializeObject<Packet>(encoding.GetString(bytes, 0, count));
				IReadOnlyDictionary<string, string> result;
				await exitLock.AcquireReaderLock();
				try{
					if(exitflag){
						return;
					}
					result = await databaseEngine.Execute(packet.reads, packet.conditions, packet.writes);
				} finally{
					exitLock.ReleaseReaderLock();
				}
				string reply = JsonConvert.SerializeObject(new Reply(packet.id, result));
				int len = encoding.GetByteCount(reply);
				bytes = null;
				try{
					bytes = arrayPool.Rent(len);
					encoding.GetBytes(reply, 0, reply.Length, bytes, 0);
					await asyncMutex.Enter();
					try
					{
						await webSocket.SendAsync(bytes.AsMemory(0, len), WebSocketMessageType.Text, true, default);
					}
					catch{
						
					}
					finally
					{
						asyncMutex.Exit();
					}
				} finally{
					if(bytes is { }){
						arrayPool.Return(bytes, false);
					}
				}
			} finally{
				releaseDisconnectionLock();
			}
		}
	}
}
