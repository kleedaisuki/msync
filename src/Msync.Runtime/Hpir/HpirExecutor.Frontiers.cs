using Msync.Core.Hpir;

namespace Msync.Runtime.Hpir;

internal static unsafe partial class HpirExecutor
{
    private static void CallWorkFrontier(HpirWork? work, HpirWorkOperation operation, HpirCall call,
        byte* frame, int destination, HpirWorkSite site, CancellationToken token)
    {
        using RuntimeTimelineScope? trace = site.Begin(operation switch
        {
            HpirWorkOperation.Mark => "hpir.work.lexical_mark",
            HpirWorkOperation.Publish => "hpir.work.lexical_publish",
            HpirWorkOperation.Join => "hpir.work.owner_join",
            _ => "hpir.work.process_join"
        });
        try
        {
            switch (operation)
            {
                case HpirWorkOperation.Mark:
                    int mark = work?.Mark ?? 0;
                    trace?.SetMetric("work.mark", mark);
                    Write(frame, destination, mark);
                    return;
                case HpirWorkOperation.Publish:
                    int first = Read<int>(frame, call.Arguments[0]);
                    trace?.SetMetric("work.mark", first);
                    work?.PublishFrontier(first, site, trace);
                    return;
                case HpirWorkOperation.Join:
                    work?.JoinFrontier(null, token, trace);
                    return;
                case HpirWorkOperation.JoinProcess:
                    nint handle = Read<nint>(frame, call.Arguments[0]);
                    int id = ((HpirProcessHandle*)handle)->Job;
                    trace?.SetMetric("work.id", id);
                    _ = work!.DemandProcess(id, completed: true, token, trace);
                    work.JoinFrontier(work.ProcessCompletion(id), token, trace);
                    Write(frame, destination, handle);
                    return;
            }
        }
        catch (Exception error) { trace?.Fail(error); throw; }
    }
}
