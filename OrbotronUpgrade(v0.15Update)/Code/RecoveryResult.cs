using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace OrbotronUpgrade
{
    internal enum RecoveryStatus { Rejected, NoChanges, RolledBack, PartialFailure, Committed, CommittedRefreshFailed }

    internal sealed class RecoveryResult
    {
        internal readonly RecoveryStatus Status;
        internal readonly string Message;
        internal readonly ReadOnlyCollection<string> AddedIds;
        internal RecoveryResult(RecoveryStatus status, string message, IList<string> added = null)
        { Status = status; Message = message; AddedIds = new List<string>(added ?? new string[0]).AsReadOnly(); }
    }
}
