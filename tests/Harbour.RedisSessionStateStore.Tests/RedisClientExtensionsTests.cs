using System.Collections.Generic;
using NUnit.Framework;
using ServiceStack;
using ServiceStack.Redis;

namespace Harbour.RedisSessionStateStore.Tests
{
    [TestFixture]
    public class RedisClientExtensionsTests : RedisTest
    {
        [Test]
        public void SetRangeInHashRaw()
        {
            redis.SetRangeInHashRaw("abc:123", new Dictionary<string, byte[]>()
            {
                { "a", "abc123".ToUtf8Bytes() },
                { "b", "1".ToUtf8Bytes() },
                { "c", "".ToUtf8Bytes() }
            });

            var result = redis.GetAllEntriesFromHash("abc:123");
            Assert.AreEqual(3, result.Count);
            Assert.AreEqual("abc123", result["a"]);
            Assert.AreEqual("1", result["b"]);
            Assert.AreEqual("", result["c"]);
        }

        [Test]
        public void GetAllEntriesFromHashRaw()
        {
            redis.SetRangeInHashRaw("abc:123", new Dictionary<string, byte[]>()
            {
                { "a", new byte[] { 1, 2, 3, 4 } },
                { "b", new byte[] { 1 } },
                { "c", new byte[0] },
            });

            var result = redis.GetAllEntriesFromHashRaw("abc:123");
            Assert.AreEqual(3, result.Count);
            Assert.AreEqual(new byte[] { 1, 2, 3, 4 }, result["a"]);
            Assert.AreEqual(new byte[] { 1 }, result["b"]);
            Assert.AreEqual(new byte[0], result["c"]);
        }

        [Test]
        public void GetValueFromHashRaw()
        {
            redis.SetRangeInHashRaw("abc:123", new Dictionary<string, byte[]>()
            {
                { "a", new byte[]{ 1, 2, 3, 4 } }
            });

            var result = redis.GetValueFromHashRaw("abc:123", "a");
            Assert.AreEqual(new byte[] { 1, 2, 3, 4 }, result);
        }

        //This test only works correctly if you run it by itself.
        [Test]
        public void SetEntryInHashIfNotExists()
        {
            redis.RemoveEntryFromHash("abc:123", "a");
            Assert.True(redis.SetEntryInHashIfNotExists("abc:123", "a", new byte[] { 1, 2, 3, 4 }));
            Assert.AreEqual(new byte[] { 1, 2, 3, 4 }, redis.GetValueFromHashRaw("abc:123", "a"));
            Assert.False(redis.SetEntryInHashIfNotExists("abc:123", "a", new byte[] { 4, 5, 6, 7 }));
            Assert.AreEqual(new byte[] { 1, 2, 3, 4 }, redis.GetValueFromHashRaw("abc:123", "a"));
        }
    }
}
