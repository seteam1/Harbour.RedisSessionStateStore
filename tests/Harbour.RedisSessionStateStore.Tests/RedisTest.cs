using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Reflection;
using ServiceStack.Logging;
using ServiceStack.Redis;

namespace Harbour.RedisSessionStateStore.Tests
{
    public abstract class RedisTest : IDisposable
    {
        // TODO: Should be different than development port!
        protected virtual string Host { get { return "cachecluster.vpc.dev.phtech.com:6379"; } }

        public IRedisClientsManager ClientManager { get; protected set; }

        protected readonly IRedisClient redis;

        protected RedisTest()
        {
            this.ClientManager = new BasicRedisClientManager(this.Host);
            this.redis = this.GetRedisClient();
            LogManager.LogFactory = new ConsoleLogFactory();
        }

        protected virtual IRedisClient GetRedisClient()
        {
            var client = this.ClientManager.GetClient();
            client.FlushAll();
            return client;
        }

        public virtual void Dispose()
        {
            this.redis.Dispose();
            this.ClientManager.Dispose();
        }
    }
}
