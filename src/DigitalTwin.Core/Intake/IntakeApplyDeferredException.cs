namespace DigitalTwin.Core.Intake;

/// <summary>
/// The processor cannot apply a captured event right now (a full rebuild holds the projection) but nothing is wrong
/// with the event: the transport retries it later without counting the attempt toward dead-lettering.
/// </summary>
public sealed class IntakeApplyDeferredException : Exception
{
    public IntakeApplyDeferredException()
    {
    }

    public IntakeApplyDeferredException(string message) : base(message)
    {
    }

    public IntakeApplyDeferredException(string message, Exception innerException) : base(message, innerException)
    {
    }
}
