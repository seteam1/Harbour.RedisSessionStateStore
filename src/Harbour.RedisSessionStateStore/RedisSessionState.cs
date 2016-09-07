using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Web.SessionState;
using System.IO;

namespace Harbour.RedisSessionStateStore
{
    internal class RedisSessionState : RedisSessionBase
    {
        public DateTime Created { get; set; }
        public bool Locked { get; set; }
        public int LockId { get; set; }
        public DateTime LockDate { get; set; }
        public int Timeout { get; set; }
        public SessionStateItemCollection Items { get; set; }
        public SessionStateActions Flags { get; set; }

        internal RedisSessionState()
        {
            this.Items = new SessionStateItemCollection();
            this.Locked = false;
            this.Created = DateTime.UtcNow;            
        }

        public IDictionary<string, byte[]> ToMap()
        {
            WriteLog(LoggingLevelEnum.Info, "==Begin ToMap==");

            var map = new Dictionary<string, byte[]>()
            {
                { "created", BitConverter.GetBytes(this.Created.Ticks) },
                { "locked", BitConverter.GetBytes(this.Locked) },
                { "lockId", this.Locked ? BitConverter.GetBytes(this.LockId) : new byte[0] },
                { "lockDate", this.Locked ? BitConverter.GetBytes(this.LockDate.Ticks) : new byte[0] },
                { "timeout", BitConverter.GetBytes(this.Timeout) },
                { "flags", BitConverter.GetBytes((int)this.Flags) }
            };

            using (var ms = new MemoryStream())
            using (var writer = new BinaryWriter(ms))
            {
                this.Items.Serialize(writer);
                map["items"] = ms.ToArray();
                writer.Close();
            }

            WriteLog(LoggingLevelEnum.Info, "==End ToMap==");
            return map;
        }

        public static bool TryParse(IDictionary<string, byte[]> raw, out RedisSessionState data)
        {
            WriteLog(LoggingLevelEnum.Info, "==BEGIN TryParse==");

            if (raw == null || raw.Count != 7)
            {
                data = null;
                WriteLog(LoggingLevelEnum.Info, "==TryParse: No Data==");
                return false;
            }

            SessionStateItemCollection sessionItems;

            using (var ms = new MemoryStream(raw["items"]))
            {
                WriteLog(LoggingLevelEnum.Info, "==TryParse: BEGIN Read Session Data==");
                if (ms.Length > 0)
                {
                    using (var reader = new BinaryReader(ms))
                    {
                        sessionItems = SessionStateItemCollection.Deserialize(reader);
                    }
                }
                else
                {
                    sessionItems = new SessionStateItemCollection();
                }
                WriteLog(LoggingLevelEnum.Info, "==TryParse: END Read Session Data==");
            }

            data = new RedisSessionState()
            {
                Created = new DateTime(BitConverter.ToInt64(raw["created"], 0)),
                Locked = BitConverter.ToBoolean(raw["locked"], 0),
                LockId = raw["lockId"].Length == 0 ? 0 : BitConverter.ToInt32(raw["lockId"], 0),
                LockDate = raw["lockDate"].Length == 0 ? DateTime.MinValue : new DateTime(BitConverter.ToInt64(raw["lockDate"], 0)),
                Timeout = BitConverter.ToInt32(raw["timeout"], 0),
                Flags = (SessionStateActions)BitConverter.ToInt32(raw["flags"], 0),
                Items = sessionItems
            };

            WriteLog(LoggingLevelEnum.Info, string.Format("==TryParse: data => Created={0} Locked={1} LockId={2} LockDate={3} Timeout={4} Flags={5}, Items={6}==",data.Created.ToShortDateString(),data.Locked, data.LockId, data.LockDate.ToShortDateString(), data.Timeout, data.Flags,data.Items));
            WriteLog(LoggingLevelEnum.Info, "==END TryParse==");
            return true;
        }
    }
}
