namespace UKBatch.Dashboard.Models.Wizard;

/// <summary>The 5 steps of the create/edit batch wizard, in order (matches the wizard stepper UI).</summary>
public enum WizardStep
{
    /// <summary>Step 1 — name (Definition).</summary>
    Definition = 0,
    /// <summary>Step 2 — the step list editor (Job / Parallel / Approval rows).</summary>
    Steps = 1,
    /// <summary>Step 3 — failure policy + optional OnFailure (compensation) steps.</summary>
    FailurePolicy = 2,
    /// <summary>Step 4 — optional cron schedule.</summary>
    Schedule = 3,
    /// <summary>Step 5 — review (DAG preview + summary) and submit.</summary>
    Review = 4,
}
