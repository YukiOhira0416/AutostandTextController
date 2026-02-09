using System;
using System.Threading;

namespace AutostandTextController
{
    /// <summary>
    /// Async-flowing scope for tagging HTTP logs with the current high-level operation name.
    /// </summary>
    internal static class HttpLogScope
    {
        private sealed class State
        {
            public string OperationName;
        }

        private static readonly AsyncLocal<State> CurrentState = new AsyncLocal<State>();

        public static string CurrentOperationName
        {
            get
            {
                var s = CurrentState.Value;
                return s == null ? null : s.OperationName;
            }
        }

        public static IDisposable Begin(string operationName)
        {
            var prev = CurrentState.Value;
            CurrentState.Value = new State { OperationName = operationName };
            return new RestoreScope(() => CurrentState.Value = prev);
        }

        private sealed class RestoreScope : IDisposable
        {
            private Action _restore;

            public RestoreScope(Action restore)
            {
                _restore = restore;
            }

            public void Dispose()
            {
                var r = Interlocked.Exchange(ref _restore, null);
                if (r != null) r();
            }
        }
    }
}
