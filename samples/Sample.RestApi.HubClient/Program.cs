using Microsoft.AspNetCore.SignalR.Client;
using UKBatch.Abstractions.Models;
using UKBatch.Api.Hub;

var hubUrl = args.Length > 0 ? args[0] : "http://localhost:5000/api/hubs/jobs";

var connection = new HubConnectionBuilder()
    .WithUrl(hubUrl)
    .WithAutomaticReconnect()
    .Build();

connection.On<JobExecution>(nameof(IJobStatusHubClient.ExecutionStateChanged), exec =>
    Console.WriteLine($"[state] {exec.ExecutionId} -> {exec.Status} (job={exec.JobName})"));

connection.On<ProgressBeat>(nameof(IJobStatusHubClient.ProgressUpdated), beat =>
    Console.WriteLine($"[progress] {beat.ExecutionId} {beat.Processed}/{beat.Total} (failed={beat.Failed})"));

connection.On<PendingApproval>(nameof(IJobStatusHubClient.ApprovalRequested), pend =>
    Console.WriteLine($"[approval] {pend.ApprovalId} batch={pend.BatchName} step={pend.BatchStepId}"));

connection.On<BatchCompletionSummary>(nameof(IJobStatusHubClient.BatchCompleted), sum =>
    Console.WriteLine($"[batch-completed] {sum.BatchName} -> {sum.FinalStatus} ({sum.SucceededJobs}/{sum.TotalJobs})"));

await connection.StartAsync();
await connection.InvokeAsync("SubscribeAll");
Console.WriteLine($"Connected to {hubUrl}; subscribed to 'all'. Ctrl+C to exit.");

var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
try { await Task.Delay(Timeout.Infinite, cts.Token); } catch (OperationCanceledException) { }
await connection.StopAsync();
