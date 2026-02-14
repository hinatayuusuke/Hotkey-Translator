namespace Hotkey_Translator.Services.Orchestration;

internal enum PipelineStopReason
{
    BlackFrame = 0,
    RoiOutOfBounds = 1,
    UnchangedHash = 2,
    NoTextDetected = 3,
    UnchangedDiff = 4,
}

internal enum PipelineOverlayAction
{
    None = 0,
    ShowLast = 1,
    Clear = 2,
    Update = 3,
}

internal readonly record struct PipelineStageResult(
    bool Continue,
    PipelineStopReason? StopReason,
    PipelineOverlayAction OverlayAction,
    string? Message = null)
{
    public static PipelineStageResult ContinueExecution()
    {
        return new PipelineStageResult(true, null, PipelineOverlayAction.None);
    }

    public static PipelineStageResult Stop(
        PipelineStopReason stopReason,
        PipelineOverlayAction overlayAction,
        string? message = null)
    {
        return new PipelineStageResult(false, stopReason, overlayAction, message);
    }
}
