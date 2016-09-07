using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using ServiceStack;
using ServiceStack.Logging;
using ServiceStack.Redis;
using ServiceStack.Text;

namespace Harbour.RedisSessionStateStore
{
    internal static class RedisClientExtensions
    {        
        private const int Success = 1;
        public static ILog Logger = LogManager.GetLogger(typeof(IRedisClient));

        public static void SetRangeInHashRaw(this IRedisClient client, string hashId, IEnumerable<KeyValuePair<string, byte[]>> keyValuePairs)
        {
            RedisSessionLogging.WriteLog(Logger,LoggingLevelEnum.Info, string.Format("==BEGIN RedisClientExtensions: SetRangeInHashRaw| client={0}, hashId={1}, keyValuePairs={2}", client,hashId, keyValuePairs.Count()));
            var keyValuePairsList = keyValuePairs.ToList();
            if (keyValuePairsList.Count == 0) return;

            var keys = new byte[keyValuePairsList.Count][];
            var values = new byte[keyValuePairsList.Count][];

            for (var i = 0; i < keyValuePairsList.Count; i++)
            {
                var kvp = keyValuePairsList[i];
                keys[i] = kvp.Key.ToUtf8Bytes();
                values[i] = kvp.Value;
            }

            ((IRedisNativeClient)client).HMSet(hashId, keys, values);
            RedisSessionLogging.WriteLog(Logger, LoggingLevelEnum.Info, string.Format("==END RedisClientExtensions: SetRangeInHashRaw| client={0}, hashId={1}", client, hashId));
        }

        public static Dictionary<string, byte[]> GetAllEntriesFromHashRaw(this IRedisClient client, string hashId)
        {
            RedisSessionLogging.WriteLog(Logger, LoggingLevelEnum.Info, string.Format("==BEGIN RedisClientExtensions: GetAllEntriesFromHashRaw| client={0}, hashId={1}", client, hashId));
            var multiData = ((IRedisNativeClient)client).HGetAll(hashId);
            RedisSessionLogging.WriteLog(Logger, LoggingLevelEnum.Info, string.Format("==END RedisClientExtensions: GetAllEntriesFromHashRaw| client={0}, hashId={1}", client, hashId));
            return MultiByteArrayToDictionary(multiData);            
        }

        internal static Dictionary<string, byte[]> MultiByteArrayToDictionary(byte[][] multiData)
        {
            RedisSessionLogging.WriteLog(Logger, LoggingLevelEnum.Info, "==BEGIN RedisClientExtensions: MultiByteArrayToDictionary");
            var map = new Dictionary<string, byte[]>();

            for (var i = 0; i < multiData.Length; i += 2)
            {
                var key = multiData[i].FromUtf8Bytes();
                map[key] = multiData[i + 1];
            }

            RedisSessionLogging.WriteLog(Logger, LoggingLevelEnum.Info, "==END RedisClientExtensions: MultiByteArrayToDictionary");
            return map;
        }

        public static byte[] GetValueFromHashRaw(this IRedisClient client, string hashId, string key)
        {
            RedisSessionLogging.WriteLog(Logger, LoggingLevelEnum.Info, string.Format("==RedisClientExtensions: GetValueFromHashRaw| client={0}, hashId={1}, key={2}",client,hashId,key));
            return ((IRedisNativeClient)client).HGet(hashId, key.ToUtf8Bytes());
        }

        public static bool SetEntryInHashIfNotExists(this IRedisClient client, string hashId, string key, byte[] value)
        {
            RedisSessionLogging.WriteLog(Logger, LoggingLevelEnum.Info, string.Format("==RedisClientExtensions: SetEntryInHashIfNotExists| client={0}, hashId={1}, key={2}, value={3}", client, hashId, key, value));
            return ((IRedisNativeClient)client).HSetNX(hashId, key.ToUtf8Bytes(), value) == Success;
        }
    }
}
