﻿namespace AsyncReaderWriterLockTests {
	using System;
	using Microsoft.VisualStudio.TestTools.UnitTesting;
	using AsyncReaderWriterLock;
	using System.Threading.Tasks;
	using System.Threading;

	[TestClass]
	public class AsyncReaderWriterLockTests {
		private const int AsyncDelay = 1000;

		private AsyncReaderWriterLock asyncLock;

		[TestInitialize]
		public void Initialize() {
			this.asyncLock = new AsyncReaderWriterLock();
		}

		[TestMethod]
		public void NoLocksHeld() {
			Assert.IsFalse(this.asyncLock.IsReadLockHeld);
			Assert.IsFalse(this.asyncLock.IsUpgradeableReadLockHeld);
			Assert.IsFalse(this.asyncLock.IsWriteLockHeld);
		}

		#region Read tests

		[TestMethod]
		public async Task SimpleReadLock() {
			Assert.IsFalse(this.asyncLock.IsReadLockHeld);
			using (await this.asyncLock.ReadLockAsync()) {
				Assert.IsTrue(this.asyncLock.IsReadLockHeld);
				await Task.Yield();
				Assert.IsTrue(this.asyncLock.IsReadLockHeld);
			}

			Assert.IsFalse(this.asyncLock.IsReadLockHeld);
		}

		[TestMethod]
		public async Task ReadLockNotIssuedToAllThreads() {
			var evt = new ManualResetEventSlim(false);
			var otherThread = Task.Run(delegate {
				evt.Wait();
				Assert.IsFalse(this.asyncLock.IsReadLockHeld);
			});

			using (await this.asyncLock.ReadLockAsync()) {
				Assert.IsTrue(this.asyncLock.IsReadLockHeld);
				evt.Set();
				await otherThread;
			}
		}

		[TestMethod]
		public async Task ReadLockImplicitSharing() {
			using (await this.asyncLock.ReadLockAsync()) {
				Assert.IsTrue(this.asyncLock.IsReadLockHeld);

				await Task.Run(delegate {
					Assert.IsTrue(this.asyncLock.IsReadLockHeld);
				});

				Assert.IsTrue(this.asyncLock.IsReadLockHeld);
			}
		}

		[TestMethod]
		public async Task ReadLockImplicitSharingCutOffByParent() {
			Task subTask;
			var outerLockReleased = new TaskCompletionSource<object>();
			using (await this.asyncLock.ReadLockAsync()) {
				Assert.IsTrue(this.asyncLock.IsReadLockHeld);

				var subTaskObservedLock = new TaskCompletionSource<object>();
				subTask = Task.Run(async delegate {
					Assert.IsTrue(this.asyncLock.IsReadLockHeld);
					subTaskObservedLock.Set();
					await outerLockReleased.Task;
					Assert.IsFalse(this.asyncLock.IsReadLockHeld);
				});

				await subTaskObservedLock.Task;
			}

			Assert.IsFalse(this.asyncLock.IsReadLockHeld);
			outerLockReleased.Set();
			await subTask;
		}

		[TestMethod]
		public async Task ConcurrentReaders() {
			var reader1HasLock = new ManualResetEventSlim();
			var reader2HasLock = new ManualResetEventSlim();
			await Task.WhenAll(
				Task.Run(async delegate {
				using (await this.asyncLock.ReadLockAsync()) {
					reader1HasLock.Set();
					reader2HasLock.Wait(); // synchronous block to ensure multiple *threads* hold lock.
				}
			}),
				Task.Run(async delegate {
				using (await this.asyncLock.ReadLockAsync()) {
					reader2HasLock.Set();
					reader1HasLock.Wait(); // synchronous block to ensure multiple *threads* hold lock.
				}
			}));
		}

		#endregion

		#region UpgradeableRead tests

		[TestMethod]
		public async Task UpgradeableReadLockNoUpgrade() {
			Assert.IsFalse(this.asyncLock.IsReadLockHeld);
			Assert.IsFalse(this.asyncLock.IsUpgradeableReadLockHeld);
			using (await this.asyncLock.UpgradeableReadLockAsync()) {
				Assert.IsTrue(this.asyncLock.IsReadLockHeld);
				Assert.IsTrue(this.asyncLock.IsUpgradeableReadLockHeld);
				await Task.Yield();
				Assert.IsTrue(this.asyncLock.IsReadLockHeld);
				Assert.IsTrue(this.asyncLock.IsUpgradeableReadLockHeld);
			}

			Assert.IsFalse(this.asyncLock.IsReadLockHeld);
			Assert.IsFalse(this.asyncLock.IsUpgradeableReadLockHeld);
		}

		[TestMethod, Timeout(1000), Ignore]
		public async Task UpgradeReadLock() {
			using (await this.asyncLock.UpgradeableReadLockAsync()) {
				Assert.IsFalse(this.asyncLock.IsWriteLockHeld);
				using (await this.asyncLock.WriteLockAsync()) {
					await Task.Yield();
					Assert.IsTrue(this.asyncLock.IsWriteLockHeld);
					Assert.IsTrue(this.asyncLock.IsUpgradeableReadLockHeld);
				}

				Assert.IsFalse(this.asyncLock.IsWriteLockHeld);
			}
		}

		#endregion

		#region Write tests

		[TestMethod]
		public async Task SimpleWriteLock() {
			Assert.IsFalse(this.asyncLock.IsWriteLockHeld);
			using (await this.asyncLock.WriteLockAsync()) {
				Assert.IsTrue(this.asyncLock.IsWriteLockHeld);
				await Task.Yield();
				Assert.IsTrue(this.asyncLock.IsWriteLockHeld);
			}

			Assert.IsFalse(this.asyncLock.IsWriteLockHeld);
		}

		#endregion

		#region Read/write lock interactions

		[TestMethod, Timeout(AsyncDelay * 2)]
		public async Task ReadersWaitForWriter() {
			var readerHasLock = new TaskCompletionSource<object>();
			var writerHasLock = new TaskCompletionSource<object>();
			await Task.WhenAll(
				Task.Run(async delegate {
				await writerHasLock.Task;
				using (await this.asyncLock.ReadLockAsync()) {
					readerHasLock.Set();
				}
			}),
				Task.Run(async delegate {
				using (await this.asyncLock.WriteLockAsync()) {
					writerHasLock.Set();
					await Task.Delay(AsyncDelay);
					Assert.IsFalse(readerHasLock.Task.IsCompleted, "Reader was issued lock while writer still had lock.");
				}
			}));
		}

		[TestMethod, Timeout(AsyncDelay * 2)]
		public async Task WriterWaitsForReaders() {
			var readerHasLock = new TaskCompletionSource<object>();
			var writerHasLock = new TaskCompletionSource<object>();
			await Task.WhenAll(
				Task.Run(async delegate {
				using (await this.asyncLock.ReadLockAsync()) {
					readerHasLock.Set();
					await Task.Delay(AsyncDelay);
					Assert.IsFalse(writerHasLock.Task.IsCompleted, "Writer was issued lock while reader still had lock.");
				}
			}),
				Task.Run(async delegate {
				await readerHasLock.Task;
				using (await this.asyncLock.WriteLockAsync()) {
					writerHasLock.Set();
					Assert.IsTrue(this.asyncLock.IsWriteLockHeld);
				}
			}));
		}

		#endregion
	}
}
