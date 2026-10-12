using System;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Claude.Bridge
{
    /// <summary>
    /// The Pipeline server echoes every failed agent command into the Console as an error
    /// ("Command 'x' failed: …", "ExecuteCommandByName: …"). The agent already gets those errors in the reply,
    /// so they're just noise in the human's Console. Drop exactly those messages; everything else passes through.
    /// </summary>
    [InitializeOnLoad]
    static class LogFilter
    {
        static LogFilter()
        {
            if (!(Debug.unityLogger.logHandler is Handler))
                Debug.unityLogger.logHandler = new Handler(Debug.unityLogger.logHandler);
        }

        static bool IsEcho(string msg) =>
            msg != null && (msg.StartsWith("ExecuteCommandByName:", StringComparison.Ordinal)
                || msg.StartsWith("Failed to handle /api/exec request", StringComparison.Ordinal)
                || (msg.StartsWith("Command '", StringComparison.Ordinal) && msg.Contains("' failed: ")));

        class Handler : ILogHandler
        {
            readonly ILogHandler m_Inner;
            public Handler(ILogHandler inner) { m_Inner = inner; }

            public void LogFormat(LogType logType, Object context, string format, params object[] args)
            {
                if (logType == LogType.Error && IsEcho(args != null && args.Length > 0 && format == "{0}" ? args[0] as string : format)) return;
                m_Inner.LogFormat(logType, context, format, args);
            }

            public void LogException(Exception exception, Object context) => m_Inner.LogException(exception, context);
        }
    }
}
