using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using ServiceStack.Logging;
using ServiceStack.Redis;

namespace Harbour.RedisSessionStateStore
{
    public enum LoggingLevelEnum
    {
        Info,
        Debug,
        Error,
        Fatal,
        Warn
    }


    public class RedisSessionBase
    {
        public static ILog Logger = LogManager.GetLogger(typeof (RedisSessionBase));

        public static void WriteLog(LoggingLevelEnum level, object message)
        {
            RedisSessionLogging.WriteLog(Logger, level, message,null);
        }

        public static void WriteLog(LoggingLevelEnum level, object message, Exception ex)
        {
            RedisSessionLogging.WriteLog(Logger, level, message, ex);
        }

        public static string FormatClientData(IRedisClient client)
        {
            return string.Format("[Host:{0} Port:{1} ConnectionTimeout:{2}]", client.Host, client.Port, client.ConnectTimeout);
        }

    }

    public static class RedisSessionLogging
    {
        public static void WriteLog(ILog logger, LoggingLevelEnum level, object message)
        {
            WriteLog(logger, level, message, null);
        }

        public static void WriteLog(ILog logger, LoggingLevelEnum level, object message, Exception ex)
        {
            switch (level)
            {
                case LoggingLevelEnum.Info: { logger.Info(message, ex); break; }
                case LoggingLevelEnum.Debug: { logger.Debug(message, ex); break; }
                case LoggingLevelEnum.Error: { logger.Error(message, ex); break; }
                case LoggingLevelEnum.Fatal: { logger.Fatal(message, ex); break; }
                case LoggingLevelEnum.Warn: { logger.Warn(message, ex); break; }
            }
        }

    }


}
