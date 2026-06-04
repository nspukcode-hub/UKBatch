using UKBatch.Abstractions.Batches;

namespace UKBatch.Dashboard.Models.Editor;

/// <summary>
/// Step 1 of the two-step palette-drop add: <c>dag-editor.js</c> reports ONLY the
/// dropped step <see cref="Kind"/> + canvas coordinates. The Editor then mints the
/// <c>WizardStepDraft</c> + <c>StepId</c> and calls back into the canvas (step 2 = <c>addNode</c>).
/// </summary>
/// <param name="Kind">The dropped palette tile's step type (parsed from the drag payload).</param>
/// <param name="X">Drop X in Drawflow canvas space.</param>
/// <param name="Y">Drop Y in Drawflow canvas space.</param>
/// <param name="IsOnFailure">
/// True ⇒ the Compensation palette tile was dropped — append a Job draft to
/// <c>BatchWizardModel.OnFailureSteps</c> (the compensation lane) instead of the main flow.
/// onFailure is a Job that lives in a different LIST, not a different <see cref="BatchStepType"/>, so
/// <see cref="Kind"/> stays <c>Job</c> and this lane flag carries the routing.
/// </param>
public sealed record class NodeDropIntent(BatchStepType Kind, double X, double Y, bool IsOnFailure = false);
