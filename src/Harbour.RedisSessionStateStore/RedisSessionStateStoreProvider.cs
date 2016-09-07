using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Web.SessionState;
using System.Collections.Specialized;
using System.Web;
using System.Web.Configuration;
using ServiceStack.Redis;
using System.Configuration.Provider;
using System.IO;
using System.Configuration;
using ServiceStack.Logging;
using ServiceStack.Redis.Support.Locking;

namespace Harbour.RedisSessionStateStore
{
    /// <summary>
    /// A SessionStateProvider implementation for Redis using the ServiceStack.Redis client.
    /// </summary>
    /// <example>
    /// In your web.config (with the <code>host</code> and <code>clientType</code>
    /// attributes being optional):
    /// <code>
    /// <![CDATA[
    ///   <system.web>
    ///     <sessionState mode="Custom" customProvider="RedisSessionStateProvider">
    ///       <providers>
    ///         <clear />
    ///         <add name="RedisSessionStateProvider" 
    ///              type="Harbour.RedisSessionStateStore.RedisSessionStateStoreProvider" 
    ///              host="localhost:6379" clientType="pooled" />
    ///       </providers>
    ///     </sessionState>
    ///   </system.web>
    /// ]]>
    /// </code>
    /// If you wish to use a custom <code>IRedisClientsManager</code>, you can 
    /// do the following in your <code>Global.asax.cs</code>:
    /// <code>
    /// <![CDATA[
    ///   private IRedisClientsManager clientManager;
    ///  
    ///   protected void Application_Start()
    ///   {
    ///       // Or use your IoC container to wire this up.
    ///       clientManager = new PooledRedisClientManager("localhost:6379");
    ///       RedisSessionStateStoreProvider.SetClientManager(clientManager);
    ///   }
    ///  
    ///   protected void Application_End()
    ///   {
    ///       clientManager.Dispose();
    ///   }
    /// ]]>
    /// </code>
    /// </example>
    public class RedisSessionStateStoreProvider : SessionStateStoreProviderBase
    {
        public static ILog Logger = LogManager.GetLogger(typeof(RedisSessionStateStoreProvider));

        private static IRedisClientsManager clientManagerStatic;
        private static RedisSessionStateStoreOptions options;
        private static object locker = new object();

        private readonly Func<HttpContext, HttpStaticObjectsCollection> staticObjectsGetter;
        private IRedisClientsManager clientManager;
        private bool manageClientManagerLifetime;
        private string name;

        /// <summary>
        /// Gets the client manager for the provider.
        /// </summary>
        public IRedisClientsManager ClientManager { get { return clientManager; } }

        internal RedisSessionStateStoreProvider(Func<HttpContext, HttpStaticObjectsCollection> staticObjectsGetter)
        {
            this.staticObjectsGetter = staticObjectsGetter;
        }

        public RedisSessionStateStoreProvider()
        {
            staticObjectsGetter = ctx => SessionStateUtility.GetSessionStaticObjects(ctx);
        }

        /// <summary>
        /// Sets the client manager to be used for the session state provider. 
        /// This client manager's lifetime will not be managed by the RedisSessionStateProvider.
        /// However, if this is not set, a client manager will be created and
        /// managed by the RedisSessionStateProvider.
        /// </summary>
        /// <param name="clientManager"></param>
        public static void SetClientManager(IRedisClientsManager clientManager)
        {
            if (clientManager == null) throw new ArgumentNullException();
            if (clientManagerStatic != null)
            {
                var exception = new InvalidOperationException("The client manager can only be configured once.");
                ThrowException(exception);
            }
            clientManagerStatic = clientManager;
        }

        private static void ThrowException(InvalidOperationException exception)
        {
            RedisSessionLogging.WriteLog(Logger, LoggingLevelEnum.Error, exception.Message, exception);
            throw exception;
        }

        public static void SetOptions(RedisSessionStateStoreOptions options)
        {
            if (options == null) throw new ArgumentNullException("options");
            if (RedisSessionStateStoreProvider.options != null)
            {
                var exception = new InvalidOperationException("The options have already been configured.");
                ThrowException(exception);                
            }

            // Clone so that we don't allow references to be modified once 
            // configured.
            RedisSessionStateStoreProvider.options = new RedisSessionStateStoreOptions(options);
        }

        internal static void ResetClientManager()
        {
            clientManagerStatic = null;
            RedisSessionLogging.WriteLog(Logger, LoggingLevelEnum.Info, "Client Manager Reset");
        }

        internal static void ResetOptions()
        {
            options = null;
            RedisSessionLogging.WriteLog(Logger, LoggingLevelEnum.Info, "Options Reset");
        }
        
        public override void Initialize(string name, NameValueCollection config)
        {
            if (String.IsNullOrWhiteSpace(name))
            {
                name = "AspNetSession";
            }

            this.name = name;
            RedisSessionLogging.WriteLog(Logger, LoggingLevelEnum.Info, string.Format("name= {0} ", name));
            lock (locker)
            {
                if (options == null)
                {
                    RedisSessionLogging.WriteLog(Logger,LoggingLevelEnum.Info, "Setting Options");
                    SetOptions(new RedisSessionStateStoreOptions());
                }

                if (clientManagerStatic == null)
                {
                    var host = config["host"];
                    var clientType = config["clientType"];

                    RedisSessionLogging.WriteLog(Logger, LoggingLevelEnum.Info, string.Format("Host= {0} ClientType= {1}", host,clientType));

                    clientManager = CreateClientManager(clientType, host);
                    manageClientManagerLifetime = true;                    
                }
                else
                {
                    clientManager = clientManagerStatic;
                    manageClientManagerLifetime = false;                    
                }
                RedisSessionLogging.WriteLog(Logger, LoggingLevelEnum.Info, string.Format("manageClientManagerLifetime= {0}, ClientType={1} ", manageClientManagerLifetime, clientManager.GetType()));
            }

            base.Initialize(name, config);
        }

        private IRedisClientsManager CreateClientManager(string clientType, string host)
        {
            if (String.IsNullOrWhiteSpace(host))
            {
                host = "localhost:6379";
            }

            if (String.IsNullOrWhiteSpace(clientType))
            {
                clientType = "POOLED";
            }

            if (clientType.ToUpper() == "POOLED")
            {
                return new PooledRedisClientManager(host);
            }
            else
            {
                return new BasicRedisClientManager(host);
            }
        }

        private IRedisClient GetClient()
        {
            return clientManager.GetClient();
        }
        
        /// <summary>
        /// Create a distributed lock for cases where more-than-a-transaction
        /// is used but we need to prevent another request from modifying the
        /// session. For example, if we need to get the session, mutate it and
        /// then write it back. We can't use *just* a transaction for this 
        /// approach because the data is returned with the rest of the commands!
        /// </summary>
        /// <param name="client"></param>
        /// <param name="key"></param>
        /// <returns></returns>
        private DisposableDistributedLock GetDistributedLock(IRedisClient client, string key)
        {
            var lockKey = key + options.KeySeparator + "lock";
            return new DisposableDistributedLock(
                client, lockKey, 
                options.DistributedLockAcquisitionTimeoutSeconds.Value, 
                options.DistributedLockTimeoutSeconds.Value
            );
        }

        private string GetSessionIdKey(string id)
        {
            var sessionIdKey = name + options.KeySeparator + id;

            RedisSessionLogging.WriteLog(Logger, LoggingLevelEnum.Info, string.Format("==RedisSessionStateStoreProvider:GetSessionIdKey {0}",sessionIdKey));
            return sessionIdKey;
        }

        public override void CreateUninitializedItem(HttpContext context, string id, int timeout)
        {
            RedisSessionLogging.WriteLog(Logger, LoggingLevelEnum.Info, "==BEGIN RedisSessionStateStoreProvider:CreateUninitializedItem");
            var key = GetSessionIdKey(id);
            using (var client = GetClient())
            {
                var state = new RedisSessionState()
                {
                    Timeout = timeout,
                    Flags = SessionStateActions.InitializeItem
                };

                UpdateSessionState(client, key, state);
            }
            RedisSessionLogging.WriteLog(Logger, LoggingLevelEnum.Info, "==END RedisSessionStateStoreProvider:CreateUninitializedItem");
        }

        public override SessionStateStoreData CreateNewStoreData(HttpContext context, int timeout)
        {
            RedisSessionLogging.WriteLog(Logger, LoggingLevelEnum.Info, string.Format("==END RedisSessionStateStoreProvider:CreateNewStoreData| Context={0}, timeout={1}",context,timeout));
            return new SessionStateStoreData(new SessionStateItemCollection(), staticObjectsGetter(context), timeout);
        }

        public override void InitializeRequest(HttpContext context)
        {
            
        }

        public override void EndRequest(HttpContext context)
        {
            
        }

        private void UseTransaction(IRedisClient client, Action<IRedisTransaction> action)
        {
            RedisSessionLogging.WriteLog(Logger, LoggingLevelEnum.Info, "==BEGIN RedisSessionStateStoreProvider:UseTransaction");
            using (var transaction = client.CreateTransaction())
            {
                action(transaction);
                transaction.Commit();
            }
            RedisSessionLogging.WriteLog(Logger, LoggingLevelEnum.Info, "==END RedisSessionStateStoreProvider:UseTransaction");
        }

        public override void ResetItemTimeout(HttpContext context, string id)
        {
            RedisSessionLogging.WriteLog(Logger, LoggingLevelEnum.Info, string.Format("==BEGIN RedisSessionStateStoreProvider:RemoveItem| Context={0}, id={1}", context, id));
            var key = GetSessionIdKey(id);
            using (var client = GetClient())
            {
                UseTransaction(client, transaction =>
                {
                    transaction.QueueCommand(c => c.ExpireEntryIn(key, TimeSpan.FromMinutes(context.Session.Timeout)));
                });
            };
            RedisSessionLogging.WriteLog(Logger, LoggingLevelEnum.Info, string.Format("==END RedisSessionStateStoreProvider:RemoveItem| Context={0}, id={1}", context, id));
        }

        public override void RemoveItem(HttpContext context, string id, object lockId, SessionStateStoreData item)
        {
            string items = "None";
            if (item != null && item.Items != null)
                items = item.Items.Count.ToString();

            RedisSessionLogging.WriteLog(Logger, LoggingLevelEnum.Info, string.Format("==BEGIN RemoveItem== RedisSessionStateStoreProvider| Context={0}, id={1}, lockId={2}, item={3}  ",context,id,lockId, items));
            var key = GetSessionIdKey(id);
            using (var client = GetClient())
            using (var distributedLock = GetDistributedLock(client, key))
            {
                if (distributedLock.LockState == DistributedLock.LOCK_NOT_ACQUIRED)
                {
                    options.OnDistributedLockNotAcquired(id);
                    RedisSessionLogging.WriteLog(Logger, LoggingLevelEnum.Info, "==END RemoveItem== RedisSessionStateStoreProvider| Lock not Acquired");
                    return;
                }

                var stateRaw = client.GetAllEntriesFromHashRaw(key);

                UseTransaction(client, transaction =>
                {
                    RedisSessionState state;
                    if (RedisSessionState.TryParse(stateRaw, out state) && state.Locked && state.LockId == (int)lockId)
                    {
                        transaction.QueueCommand(c => c.Remove(key));
                    }
                    var stateString = "None";
                    if (state != null)
                        stateString = string.Format("Created:{0}, LockId:{1}, Locked:{2}, LockDate:{3}", state.Created, state.LockId, state.Locked, state.LockDate);
                    
                    RedisSessionLogging.WriteLog(Logger, LoggingLevelEnum.Info, string.Format("RedisSessionStateStoreProvider: state => {0}",stateString));
                });
            }
            RedisSessionLogging.WriteLog(Logger, LoggingLevelEnum.Info, "==END RemoveItem== RedisSessionStateStoreProvider");
        }

        public override SessionStateStoreData GetItem(HttpContext context, string id, out bool locked, out TimeSpan lockAge, out object lockId, out SessionStateActions actions)
        {            
            return GetItem(false, context, id, out locked, out lockAge, out lockId, out actions);
        }

        public override SessionStateStoreData GetItemExclusive(HttpContext context, string id, out bool locked, out TimeSpan lockAge, out object lockId, out SessionStateActions actions)
        {            
            return GetItem(true, context, id, out locked, out lockAge, out lockId, out actions);
        }

        private SessionStateStoreData GetItem(bool isExclusive, HttpContext context, string id, out bool locked, out TimeSpan lockAge, out object lockId, out SessionStateActions actions)
        {
            RedisSessionLogging.WriteLog(Logger, LoggingLevelEnum.Info, string.Format("==BEGIN RedisSessionStateStoreProvider:GetItem| Context={0}, id={1} ", context, id));
            locked = false;
            lockAge = TimeSpan.Zero;
            lockId = null;
            actions = SessionStateActions.None;
            SessionStateStoreData result = null;

            var key = GetSessionIdKey(id);
            using (var client = GetClient())
            using (var distributedLock = GetDistributedLock(client, key))
            {
                if (distributedLock.LockState == DistributedLock.LOCK_NOT_ACQUIRED)
                {
                    RedisSessionLogging.WriteLog(Logger, LoggingLevelEnum.Info, string.Format("==GetItem:DistributedLock.LOCK_NOT_ACQUIRED| Context={0}, id={1} ", context, id));
                    options.OnDistributedLockNotAcquired(id);
                    return null;
                }

                var stateRaw = client.GetAllEntriesFromHashRaw(key);

                RedisSessionState state;
                if (!RedisSessionState.TryParse(stateRaw, out state))
                {
                    RedisSessionLogging.WriteLog(Logger, LoggingLevelEnum.Info, string.Format("==GetItem:RedisSessionState| Context={0}, id={1} ", context, id));
                    return null;
                }

                actions = state.Flags;

                if (state.Locked)
                {
                    locked = true;
                    lockId = state.LockId;
                    lockAge = DateTime.UtcNow - state.LockDate;
                    RedisSessionLogging.WriteLog(Logger, LoggingLevelEnum.Info, string.Format("==GetItem:Locked State| locked={0}, lockId={1}, lockAge={2} ", locked, lockId, lockAge));
                    return null;
                }

                if (isExclusive)
                {
                    locked = state.Locked = true;
                    state.LockDate = DateTime.UtcNow;
                    lockAge = TimeSpan.Zero;
                    lockId = ++state.LockId;
                    RedisSessionLogging.WriteLog(Logger, LoggingLevelEnum.Info, string.Format("==GetItem:IsExclusive| locked={0}, lockId={1}, lockAge={2}, lockDate={3} ", locked, lockId, lockAge, state.LockDate));
                }

                state.Flags = SessionStateActions.None;

                UseTransaction(client, transaction =>
                {
                    transaction.QueueCommand(c => c.SetRangeInHashRaw(key, state.ToMap()));
                    transaction.QueueCommand(c => c.ExpireEntryIn(key, TimeSpan.FromMinutes(state.Timeout)));
                });

                var items = actions == SessionStateActions.InitializeItem ? new SessionStateItemCollection() : state.Items;

                result = new SessionStateStoreData(items, staticObjectsGetter(context), state.Timeout);
            }
            RedisSessionLogging.WriteLog(Logger, LoggingLevelEnum.Info, string.Format("==END RedisSessionStateStoreProvider:GetItem| Context={0}, id={1}, locked={2}, lockAge={3}, lockId={4} ", context, id, locked, lockAge, lockId));
            return result;
        }        

        public override void ReleaseItemExclusive(HttpContext context, string id, object lockId)
        {
            RedisSessionLogging.WriteLog(Logger, LoggingLevelEnum.Info, string.Format("==BEGIN RedisSessionStateStoreProvider:ReleaseItemExclusive| Context={0}, id={1}, lockId={2} ", context, id, lockId));
            using (var client = GetClient())
            {
                UpdateSessionStateIfLocked(client, id, (int)lockId, state =>
                {
                    state.Locked = false;
                    state.Timeout = context.Session.Timeout;
                });
            }
            RedisSessionLogging.WriteLog(Logger, LoggingLevelEnum.Info, string.Format("==END RedisSessionStateStoreProvider:ReleaseItemExclusive| Context={0}, id={1}, lockId={2} ", context, id, lockId));
        }

        public override void SetAndReleaseItemExclusive(HttpContext context, string id, SessionStateStoreData item, object lockId, bool newItem)
        {
            RedisSessionLogging.WriteLog(Logger, LoggingLevelEnum.Info, string.Format("==BEGIN RedisSessionStateStoreProvider:SetAndReleaseItemExclusive| Context={0}, id={1}, lockId={2} ", context, id, lockId));
            using (var client = GetClient())
            {
                if (newItem)
                {
                    var state = new RedisSessionState()
                    {
                        Items = (SessionStateItemCollection)item.Items,
                        Timeout = item.Timeout,
                    };

                    var key = GetSessionIdKey(id);
                    UpdateSessionState(client, key, state);                    
                }
                else
                {
                    RedisSessionLogging.WriteLog(Logger, LoggingLevelEnum.Info, string.Format("==RedisSessionStateStoreProvider:UpdateSessionState| Context={0}, id={1}, lockId={2} ", context, id, lockId));
                    UpdateSessionStateIfLocked(client, id, (int)lockId, state =>
                    {
                        state.Items = (SessionStateItemCollection)item.Items;
                        state.Locked = false;
                        state.Timeout = item.Timeout;
                    });
                }
            }
            RedisSessionLogging.WriteLog(Logger, LoggingLevelEnum.Info, string.Format("==END RedisSessionStateStoreProvider:SetAndReleaseItemExclusive| Context={0}, id={1}, lockId={2} ", context, id, lockId));
        }

        private void UpdateSessionStateIfLocked(IRedisClient client, string id, int lockId, Action<RedisSessionState> stateAction)
        {
            RedisSessionLogging.WriteLog(Logger, LoggingLevelEnum.Info, string.Format("==BEGIN RedisSessionStateStoreProvider:UpdateSessionStateIfLocked| client={0}, lockId={1} ", client, lockId));
            var key = GetSessionIdKey(id);
            using (var distributedLock = GetDistributedLock(client, key))
            {
                if (distributedLock.LockState == DistributedLock.LOCK_NOT_ACQUIRED)
                {
                    options.OnDistributedLockNotAcquired(id);
                    RedisSessionLogging.WriteLog(Logger, LoggingLevelEnum.Info, string.Format("==END RedisSessionStateStoreProvider:UpdateSessionStateIfLocked| Lock Not Acquired client={0}, key={1}, lockId={2}  ", client, key, lockId));
                    return;
                }

                var stateRaw = client.GetAllEntriesFromHashRaw(key);
                RedisSessionState state;
                if (RedisSessionState.TryParse(stateRaw, out state) && state.Locked && state.LockId == lockId)
                {
                    stateAction(state);
                    UpdateSessionState(client, key, state);
                }
            }
            RedisSessionLogging.WriteLog(Logger, LoggingLevelEnum.Info, string.Format("==END RedisSessionStateStoreProvider:UpdateSessionStateIfLocked| client={0}, key={1}, lockId={2}  ", client, key, lockId));
        }

        private void UpdateSessionState(IRedisClient client, string key, RedisSessionState state)
        {
            RedisSessionLogging.WriteLog(Logger, LoggingLevelEnum.Info, string.Format("==BEGIN RedisSessionStateStoreProvider:UpdateSessionState| client={0}, key={1}, state={2} ", client, key,state));
            UseTransaction(client, transaction =>
            {
                transaction.QueueCommand(c => c.SetRangeInHashRaw(key, state.ToMap()));
                transaction.QueueCommand(c => c.ExpireEntryIn(key, TimeSpan.FromMinutes(state.Timeout)));
            });
            RedisSessionLogging.WriteLog(Logger, LoggingLevelEnum.Info, string.Format("==END RedisSessionStateStoreProvider:UpdateSessionState| client={0}, key={1}, state={2}  ", client,key,state));
        }

        public override bool SetItemExpireCallback(SessionStateItemExpireCallback expireCallback)
        {
            // Redis < 2.8 doesn't easily support key expiry notifications.
            // As of Redis 2.8, keyspace notifications (http://redis.io/topics/notifications)
            // can be used. Therefore, if you'd like to support the expiry
            // callback and are using Redis 2.8, you can inherit from this
            // class and implement it.
            return false;
        }

        public override void Dispose()
        {
            RedisSessionLogging.WriteLog(Logger, LoggingLevelEnum.Info, string.Format("==BEGIN RedisSessionStateStoreProvider:Dispose"));
            if (manageClientManagerLifetime)
            {
                clientManager.Dispose();
            }
            RedisSessionLogging.WriteLog(Logger, LoggingLevelEnum.Info, string.Format("==END RedisSessionStateStoreProvider:Dispose"));
        }
    }
}
