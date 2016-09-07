using System;
using System.Collections.Generic;
using System.Web.SessionState;
using NUnit.Framework;

namespace Harbour.RedisSessionStateStore.Tests
{
    [TestFixture]
    public class RedisSessionStateTests
    {
        // 2011-12-22 at 1:1:1 UTC
        private byte[] date1Bytes = new byte[] { 128, 68, 113, 78, 92, 142, 206, 8 };

        // 2011-11-22 at 1:1:1 UTC
        private byte[] date2Bytes = new byte[] { 128, 196, 12, 86, 201, 118, 206, 8 };
        
        // { name: "Felix", age: 1 }
        private byte[] itemsBytes = new byte[] { 2, 0, 0, 0, 255, 255, 255, 255, 4, 110, 97, 109, 101, 3, 97, 103, 101, 7, 0, 0, 0, 12, 0, 0, 0, 1, 5, 70, 101, 108, 105, 120, 2, 1, 0, 0, 0 };
        
        // 999
        private byte[] lockIdBytes = new byte[] { 231, 3, 0, 0 };

        [Test]
        public void ToMap()
        {
            var data = new RedisSessionState()
            {
                Created = new DateTime(2011, 12, 22, 1, 1, 1, DateTimeKind.Utc),
                Locked = true,
                LockId = 999,
                LockDate = new DateTime(2011, 11, 22, 1, 1, 1, DateTimeKind.Utc),
                Timeout = 3,
                Flags = SessionStateActions.InitializeItem
            };

            data.Items["name"] = "Felix";
            data.Items["age"] = 1;

            var map = data.ToMap();
            Assert.AreEqual(date1Bytes, map["created"]);
            Assert.AreEqual(new byte[] { 1 }, map["locked"]);
            Assert.AreEqual(lockIdBytes, map["lockId"]);
            Assert.AreEqual(date2Bytes, map["lockDate"]);
            Assert.AreEqual(new byte[] { 3, 0, 0, 0 }, map["timeout"]);
            Assert.AreEqual(new byte[] { 1, 0, 0, 0 }, map["flags"]);
            Assert.AreEqual(itemsBytes, map["items"]);
        }

        [Test]
        public void TryParse_should_fail_if_null_data()
        {
            RedisSessionState data;
            Assert.False(RedisSessionState.TryParse(null, out data));
        }

        [Test]
        public void TryParse_should_fail_if_incorrect_length()
        {
            RedisSessionState data;
            var raw = new Dictionary<string, byte[]>();
            Assert.False(RedisSessionState.TryParse(raw, out data));
        }

        [Test]
        public void TryParse_should_pass_with_valid_data()
        {
            var raw = new Dictionary<string, byte[]>()
            {
                { "created", date1Bytes },
                { "locked", new byte[] { 1 } },
                { "lockId", lockIdBytes },
                { "lockDate", date2Bytes },
                { "timeout", new byte[] { 3, 0, 0, 0 } },
                { "flags", new byte[] { 1, 0, 0, 0 } },
                { "items", itemsBytes }
            };

            RedisSessionState data;
            Assert.True(RedisSessionState.TryParse(raw, out data));
            Assert.AreEqual(new DateTime(2011, 12, 22, 1, 1, 1, DateTimeKind.Utc), data.Created);
            Assert.True(data.Locked);
            Assert.AreEqual(999, data.LockId);
            Assert.AreEqual(new DateTime(2011, 11, 22, 1, 1, 1, DateTimeKind.Utc), data.LockDate);
            Assert.AreEqual(3, data.Timeout);
            Assert.AreEqual(SessionStateActions.InitializeItem, data.Flags);
            Assert.AreEqual(2, data.Items.Count);
            Assert.AreEqual("Felix", data.Items["name"]);
            Assert.AreEqual(1, data.Items["age"]);
        }
    }
}
