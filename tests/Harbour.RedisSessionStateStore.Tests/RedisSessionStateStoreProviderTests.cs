using System;
using System.Collections.Generic;
using System.Linq;
using System.Collections.Specialized;
using ServiceStack.Redis;
using System.Web;
using Moq;
using System.Web.SessionState;
using System.Collections;
using System.Configuration;
using System.IO;
using System.Runtime.Serialization;
using System.Reflection;
using NUnit.Framework;
using Serilog;
using ServiceStack.Logging;

namespace Harbour.RedisSessionStateStore.Tests
{
    [TestFixture]
    public class RedisSessionStateStoreProviderTests : RedisTest
    {
        private const string KeyName = "Harbour";
        private const string key = KeyName + "/1234";

        private readonly SessionStateItemCollection itemsA;

        public RedisSessionStateStoreProviderTests()
        {
            this.itemsA = new SessionStateItemCollection();
            this.itemsA["name"] = "Felix";
            this.itemsA["age"] = 1;
        }


        public class SetupArgs
        {
            public string SessionId { get; set; }
            public int Timeout { get; set; }
            public bool CreateSession { get; set; }

            public SetupArgs()
            {
                SessionId = "1234";
                Timeout = 555;
                CreateSession = true;
            }
        }
        public RedisSessionStateStoreProvider DataSetup(SetupArgs args)
        {
            var sessionProvider = this.CreateProvider();
            redis.Remove("Harbour/1234");
            if (args.CreateSession)
            {
                sessionProvider.CreateUninitializedItem(null, args.SessionId, args.Timeout);
            }
            return sessionProvider;
        }

        [Test]
        public void TestLogFileCreated()
        {
            var config = new LoggerConfiguration().ReadFrom.AppSettings();
            Log.Logger = config.CreateLogger();


            var provider = new RedisSessionStateStoreProvider();
            provider.Initialize("APP_NAME", new NameValueCollection()
            {
                { "Host", "9.9.9.9:999" },
                { "clientType", "pooled" }
            });
            Assert.IsInstanceOf<PooledRedisClientManager>(provider.ClientManager);

            var today = DateTime.Now;
            var path = "D:\\Log\\HarbourSession\\HarbourSessionProvider-" + today.ToString("yyyyMMdd") + ".txt";
            Log.Logger = new LoggerConfiguration().WriteTo.ColoredConsole().CreateLogger();

            //TODO: Once we can upgrade to Serilog 2.0+, just remove the exception assertion but keep the guts
            Assert.Throws<IOException>(() =>
            {
                using (var fs = new StreamReader(path))
                {
                    Assert.IsNotEmpty(fs.ReadToEnd());
                }
            });
        }

        [Test]
        public void Initialize_with_no_configured_clients_manager_can_create_pooled_clients_manager()
        {
            var provider = new RedisSessionStateStoreProvider();
            provider.Initialize("APP_NAME", new NameValueCollection()
            {
                { "Host", "9.9.9.9:999" },
                { "clientType", "pooled" }
            });
            Assert.IsInstanceOf<PooledRedisClientManager>(provider.ClientManager);
        }

        [Test]
        public void Initialize_with_no_configured_clients_manager_can_create_basic_clients_manager()
        {
            var provider = new RedisSessionStateStoreProvider();
            provider.Initialize("APP_NAME", new NameValueCollection()
            {
                { "Host", "9.9.9.9:999" },
                { "clientType", "basic" }
            });
            Assert.IsInstanceOf<BasicRedisClientManager>(provider.ClientManager);
        }

        [Test]
        public void Initialize_with_specified_clients_manager_should_not_manage_lifetime()
        {
            try
            {
                var clientManager = new Mock<IRedisClientsManager>();
                RedisSessionStateStoreProvider.SetClientManager(clientManager.Object);
                var provider = new RedisSessionStateStoreProvider();
                provider.Initialize(KeyName, new NameValueCollection());

                Assert.AreSame(clientManager.Object, provider.ClientManager);

                provider.Dispose();

                clientManager.Verify(m => m.Dispose(), Times.Never());
            }
            finally
            {
                RedisSessionStateStoreProvider.ResetClientManager();
            }
        }

        [Test]
        public void SetItemExpireCallback_is_not_supported()
        {
            Assert.False(this.CreateProvider().SetItemExpireCallback((x, y) => { }));
        }

        [Test]
        public void CreateUnitializedItem()
        {
            DataSetup(new SetupArgs());
            AssertState(key,
                locked: false, lockId: 0, lockDate: DateTime.MinValue,
                timeout: 555, flags: SessionStateActions.InitializeItem);
        }

        [Test]
        public void ResetItemTimeout()
        {
            var provider = DataSetup(new SetupArgs());
            var validSessionId = "1234";

            redis.SetSessionState(key, new RedisSessionState());
            redis.ExpireEntryIn(key, TimeSpan.FromMinutes(10));
            var httpContext = CreateHttpContextWithSession(sessionTimeout: 20);

            provider.ResetItemTimeout(httpContext, validSessionId);

            var ttl = redis.GetTimeToLive(key);
            Assert.AreEqual(20, ttl.Value.TotalMinutes);
        }

        [Test]
        public void RemoveItem_should_remove_if_lockId_matches()
        {
            var provider = DataSetup(new SetupArgs());
            var validLockId = 999;
            var validSessionId = "1234";

            redis.SetSessionState(key, new RedisSessionState()
            {
                Locked = true,
                LockDate = DateTime.UtcNow,
                LockId = validLockId
            });

            provider.RemoveItem(null, validSessionId, validLockId, null);
            Assert.False(redis.ContainsKey(key));
        }

        [Test]
        public void RemoveItem_should_not_remove_if_lockId_does_not_match()
        {
            var provider = DataSetup(new SetupArgs());
            var validLockId = 999;
            var invalidLockId = 111;
            var validSessionId = "1234";

            redis.SetSessionState(key, new RedisSessionState()
            {
                Locked = true,
                LockDate = DateTime.UtcNow,
                LockId = validLockId
            });

            provider.RemoveItem(null, validSessionId, invalidLockId, null);
            Assert.True(redis.ContainsKey(key));
        }

        [Test]
        public void RemoveItem_should_not_remove_if_session_id_does_not_exist()
        {
            var provider = DataSetup(new SetupArgs());
            var validLockId = 999;
            var invalidLockId = 111;
            var validSessionId = "1234";
            var invalidSessionId = "5678";

            redis.SetSessionState(key, new RedisSessionState()
            {
                Locked = true,
                LockDate = DateTime.UtcNow,
                LockId = validLockId
            });

            provider.RemoveItem(null, invalidSessionId, validLockId, null);
            Assert.True(redis.ContainsKey(key));
        }

        [Test]
        public void ReleaseItemExclusive_should_not_remove_lock_if_lockId_does_not_match()
        {
            var provider = DataSetup(new SetupArgs());

            var lockDate = DateTime.UtcNow;
            var validLockId = 999;
            var invalidLockId = 111;
            var validSessionId = "1234";

            redis.SetSessionState(key, new RedisSessionState()
            {
                Locked = true,
                LockId = validLockId,
                LockDate = lockDate
            });

            //change lock id to something different from what we're expecting
            provider.ReleaseItemExclusive(null, validSessionId, invalidLockId);

            AssertState(key,
                locked: true, lockId: validLockId, lockDate: lockDate);
        }

        [Test]
        public void ReleaseItemExclusive_should_not_remove_lock_if_session_id_does_not_match()
        {
            var provider = DataSetup(new SetupArgs());
            
            var lockDate = DateTime.UtcNow;
            var validLockId = 999;
            var invalidSessionId = "5678";

            redis.SetSessionState(key, new RedisSessionState()
            {
                Locked = true,
                LockId = validLockId,
                LockDate = lockDate
            });

            //try to release session with key other than what we have
            provider.ReleaseItemExclusive(null, invalidSessionId, validLockId);

            //Show that session still has the lock
            AssertState(key,
                locked: true, lockId: validLockId, lockDate: lockDate);
        }

        [Test]
        public void ReleaseItemExclusive_should_clear_lock_and_reset_timeout_for_locked_session()
        {
            var provider = DataSetup(new SetupArgs());
            var lockDate = DateTime.UtcNow;
            var validLockId = 999;
            var validSessionId = "1234";
            
            var httpContext = CreateHttpContextWithSession(sessionTimeout: 20);

            redis.SetSessionState(key, new RedisSessionState()
            {
                Locked = true,
                LockId = validLockId,
                LockDate = lockDate
            });

            provider.ReleaseItemExclusive(httpContext, validSessionId, validLockId);

            AssertState(key,
                locked: false, lockId: 0, lockDate: DateTime.MinValue,
                ttl: 20);
        }

        [Test]
        public void SetAndReleaseItemExclusive_should_add_new_item_and_set_timeout_if_newItem_is_true()
        {
            var provider = DataSetup(new SetupArgs());
            var validSessionId = "1234";
            
            var item = this.CreateSessionStoreData(333, new Dictionary<string, object>()
                 {
                     { "name", "Felix" },
                     { "age", 1 }
                 });

            provider.SetAndReleaseItemExclusive(null, validSessionId, item, null, true);

            AssertState(key,
                locked: false,
                flags: 0,
                timeout: 333,
                items: new Hashtable()
                {
                         { "name", "Felix" },
                         { "age", 1 }
                });
        }

        [Test]
        public void SetAndReleaseItemExclusive_should_not_update_if_lock_id_does_not_match()
        {
            var provider = DataSetup(new SetupArgs());

            var lockDate = DateTime.UtcNow;
            var validLockId = 999;
            var invalidLockId = 111;
            var validSessionId = "1234";
            
            redis.SetSessionState(key, new RedisSessionState()
            {
                Locked = true,
                LockId = validLockId,
                LockDate = lockDate
            });

            var item = this.CreateSessionStoreData(333, new Dictionary<string, object>());

            provider.SetAndReleaseItemExclusive(null, validSessionId, item, invalidLockId, false);

            AssertState(key,
                locked: true, lockId: validLockId, lockDate: lockDate);
        }

        [Test]
        public void SetAndReleaseItemExclusive_should_update_items_and_release_lock_for_locked_session()
        {
            var provider = DataSetup(new SetupArgs());
            var validSessionId = "1234";
            var validLockId = 1;

            redis.SetSessionState(key, new RedisSessionState()
            {
                Locked = true,
                LockId = validLockId,
                LockDate = DateTime.UtcNow,
                Items = itemsA
            });

            var updatedItems = this.CreateSessionStoreData(999, new Dictionary<string, object>()
                 {
                     { "name", "Daisy" },
                     { "age", 3 }
                 });

            provider.SetAndReleaseItemExclusive(null, validSessionId, updatedItems, validLockId, false);

            AssertState(key,
                locked: false, ttl: 999,
                items: new Hashtable()
                {
                             { "name", "Daisy" },
                             { "age", 3 }
                });
        }

        [Test]
        public void GetItem_should_return_null_and_not_locked_if_no_session_item_is_found()
        {
            var provider = DataSetup(new SetupArgs() { CreateSession = false });
            var invalidSessionId = "1234";
            
            bool locked;
            TimeSpan lockAge;
            object lockId;
            SessionStateActions actions;

            var data = provider.GetItem(null, invalidSessionId, out locked, out lockAge, out lockId, out actions);

            Assert.Null(data);
            Assert.False(locked);
            Assert.AreEqual(SessionStateActions.None, actions);
        }

        [Test]
        public void GetItem_should_return_null_and_locked_if_session_item_is_found_but_is_locked()
        {
            var provider = DataSetup(new SetupArgs());
            var lockDate = DateTime.UtcNow.AddHours(-1);
            var validLockId = 999;
            var validSessionId = "1234";
            
            redis.SetSessionState(key, new RedisSessionState()
            {
                Locked = true,
                LockDate = lockDate,
                LockId = validLockId
            });

            bool locked;
            TimeSpan lockAge;
            object lockId;
            SessionStateActions actions;

            var data = provider.GetItem(null, validSessionId, out locked, out lockAge, out lockId, out actions);

            Assert.Null(data);
            Assert.True(locked);
            AssertInRange(TimeSpan.FromHours(1), lockAge);
            Assert.AreEqual(validLockId, lockId);
            Assert.AreEqual(SessionStateActions.None, actions);
        }

        [Test]
        public void GetItem_should_return_data_and_extend_session_if_session_found_and_not_locked()
        {
            var provider = DataSetup(new SetupArgs());
            var validSessionId = "1234";

            redis.SetSessionState(key, new RedisSessionState()
            {
                Flags = SessionStateActions.None,
                Items = itemsA,
                Timeout = 80
            });

            bool locked;
            TimeSpan lockAge;
            object lockId;
            SessionStateActions actions;

            var data = provider.GetItem(null, validSessionId, out locked, out lockAge, out lockId, out actions);
            var ttl = redis.GetTimeToLive(key);
            Assert.AreEqual(80, data.Timeout);
            Assert.AreEqual(80, ttl.Value.TotalMinutes);

            Assert.False(locked);
            Assert.AreEqual(SessionStateActions.None, actions);

            AssertStateItems(new Hashtable()
                 {
                     { "name", "Felix" },
                     { "age", 1 }
                 }, data.Items);
        }

        [Test]
        public void GetItemExclusive_should_return_null_and_not_locked_if_no_session_item_is_found()
        {
            var provider = DataSetup(new SetupArgs());
            var invalidSessionId = "5678";

            bool locked;
            TimeSpan lockAge;
            object lockId;
            SessionStateActions actions;

            var data = provider.GetItemExclusive(null, invalidSessionId, out locked, out lockAge, out lockId, out actions);

            Assert.Null(data);
            Assert.False(locked);
            Assert.AreEqual(SessionStateActions.None, actions);
        }

        [Test]
        public void GetItemExclusive_should_return_null_and_locked_if_session_item_is_found_but_is_locked()
        {
            var provider = DataSetup(new SetupArgs());
            var validSessionId = "1234";
            var invalidSessionId = "5678";
            var validLockId = 1;
            var lockDate = DateTime.UtcNow.AddHours(-1);

            redis.SetSessionState(key, new RedisSessionState()
            {
                Locked = true,
                LockDate = lockDate,
                LockId = validLockId
            });

            bool locked;
            TimeSpan lockAge;
            object lockId;
            SessionStateActions actions;

            var data = provider.GetItemExclusive(null, validSessionId, out locked, out lockAge, out lockId, out actions);

            Assert.Null(data);
            Assert.True(locked);
            AssertInRange(TimeSpan.FromHours(1), lockAge);
            Assert.AreEqual(validLockId, lockId);
            Assert.AreEqual(SessionStateActions.None, actions);
        }

        [Test]
        public void GetItemExclusive_should_return_data_and_lock_session_and_extend_session_if_session_found_and_not_locked()
        {
            var provider = DataSetup(new SetupArgs());
            var validSessionId = "1234";
            var validLockId = 1;

            redis.SetSessionState(key, new RedisSessionState()
            {
                Locked = false,
                Flags = SessionStateActions.None,
                Items = itemsA,
                Timeout = 80
            });

            bool locked;
            TimeSpan lockAge;
            object lockId;
            SessionStateActions actions;

            var data = provider.GetItemExclusive(null, validSessionId, out locked, out lockAge, out lockId, out actions);

            AssertState(key,
                ttl: 80,
                locked: true,
                lockId: validLockId,
                lockDate: DateTime.UtcNow);

            Assert.True(locked);
            Assert.AreEqual(TimeSpan.Zero, lockAge);
            Assert.AreEqual(validLockId, lockId);
            Assert.AreEqual(SessionStateActions.None, actions);

            Assert.AreEqual(80, data.Timeout);
            AssertStateItems(new Hashtable()
                 {
                     { "name", "Felix" },
                     { "age", 1 }
                 }, data.Items);
        }

        private void AssertStateItems(IDictionary expected, ISessionStateItemCollection actual)
        {
            Assert.AreEqual(expected.Count, actual.Count);
            foreach (var kvp in expected.Cast<DictionaryEntry>())
            {
                var name = (string)kvp.Key;
                Assert.AreEqual(kvp.Value, actual[name]);
            }
        }

        private RedisSessionState AssertState(string key, bool? locked = null, int? lockId = null, DateTime? lockDate = null, int? timeout = null, SessionStateActions? flags = null, int? ttl = null, IDictionary items = null)
        {
            var data = redis.GetSessionState(key);
            var ttlActual = redis.GetTimeToLive(key);

            if (ttl != null || timeout.HasValue)
            {
                var t = (ttl ?? (int)timeout) * 60;
                Assert.True(Math.Abs(t - ttlActual.Value.TotalSeconds) < 10);
            }

            AssertCloseEnough(DateTime.UtcNow, data.Created);

            if (locked.HasValue) Assert.AreEqual(locked, data.Locked);
            if (lockId.HasValue) Assert.AreEqual(lockId, data.LockId);
            if (lockDate.HasValue) AssertCloseEnough(lockDate.Value, data.LockDate);
            if (timeout.HasValue) Assert.AreEqual(timeout, data.Timeout);
            if (flags.HasValue) Assert.AreEqual(flags, data.Flags);

            if (items != null)
            {
                AssertStateItems(items, data.Items);
            }

            return data;
        }

        private SessionStateStoreData CreateSessionStoreData(int timeout, IDictionary<string, object> itemsMap)
        {
            var items = new SessionStateItemCollection();
            foreach (var kvp in itemsMap)
            {
                items[kvp.Key] = kvp.Value;
            }

            return new SessionStateStoreData(items, new HttpStaticObjectsCollection(), timeout);
        }

        private void AssertCloseEnough(DateTime expected, DateTime actual, int fuzzSeconds = 10)
        {
            Assert.True((actual - expected).TotalSeconds < fuzzSeconds, "Dates close enough.");
        }

        private void AssertInRange(TimeSpan expected, TimeSpan actual, int fuzz = 10)
        {
            Assert.True(Math.Abs(expected.TotalSeconds - actual.TotalSeconds) < fuzz);
        }

        private RedisSessionStateStoreProvider CreateProvider(string host = null)
        {
            var provider = new RedisSessionStateStoreProvider(ctx => new HttpStaticObjectsCollection());

            provider.Initialize(KeyName, new NameValueCollection()
            {
                { "Host", host ?? this.Host },
                { "clientType", "basic" }
            });

            return provider;
        }

		private HttpContext CreateHttpContextWithSession(int sessionTimeout)
		{
			var httpRequest = new HttpRequest("foo.html", "http://localhost/foo.html", "");
			var httpResponse = new HttpResponse(TextWriter.Null);
			var httpContext = new HttpContext(httpRequest, httpResponse);

			// HACK: Initialize the session since we don't want to use the Web.config.
			var httpSession = (HttpSessionState)FormatterServices.GetUninitializedObject(typeof(HttpSessionState));
			typeof(HttpSessionState).GetField("_container", BindingFlags.NonPublic | BindingFlags.Instance)
				.SetValue(httpSession, new Mock<IHttpSessionState>().SetupAllProperties().Object);
			httpSession.Timeout = sessionTimeout;
			httpContext.Items["AspSession"] = httpSession;

			return httpContext;
		}
    }

    internal static class RedisClientExtensions
    {
        public static RedisSessionState GetSessionState(this IRedisClient redis, string key)
        {
            RedisSessionState state = null;
            RedisSessionState.TryParse(redis.GetAllEntriesFromHashRaw(key), out state);
            return state;
        }

        public static void SetSessionState(this IRedisClient redis, string key, RedisSessionState state)
        {
            redis.SetRangeInHashRaw(key, state.ToMap());
        }
    }
}
