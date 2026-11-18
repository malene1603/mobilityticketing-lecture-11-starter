using System.Security.Cryptography;

namespace MobilityTicketing.Reports.Application;

public sealed class ReportWorkflow(
    IReportObjectStore storage, IReportCatalogue catalogue, IUploadReceipts receipts, ExportLocation destination)
{
    private readonly IReportObjectStore _storage = storage;
    private readonly IReportCatalogue _catalogue = catalogue;
    private readonly IUploadReceipts _receipts = receipts;
    private readonly ExportLocation _destination = destination;

    public async Task<string> StageAsync(string reportId, byte[] bytes)
    {
        var sha256 = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        var rowCount = bytes.Count(b => b == (byte)'\n');
        
        _receipts.Reserve(reportId);
        
        var versionId = await _storage.UploadAsync(_destination, bytes);

        if (string.IsNullOrWhiteSpace(versionId) || versionId is "null" or "None")
            throw new InvalidOperationException("InvalidVersionId");
        
        var input = new ReportInput(
            reportId,
            _destination.Bucket,
            _destination.Key,
            versionId,
            sha256,
            rowCount);

        await _receipts.WriteAsync(input);

        return versionId;
    }

    public async Task<string> PublishAsync(string reportId, byte[] bytes, string? supersedes)
    {
        await StageAsync(reportId, bytes);

        return await RecoverAsync(reportId, supersedes);
    }

    public async Task<string> RecoverAsync(string reportId, string? supersedes)
    {
        var input = await _receipts.ReadAsync(reportId);

        var verification = await CheckAsync(input);

        if (verification != Verification.Matches)
        {
            throw new InvalidOperationException(
                $"Report verification failed: {verification}");
        }

        await _catalogue.SaveAsync(input, supersedes);

        return reportId;
    }

    public async Task<Verification> VerifyAsync(string reportId)
    {
        ReportInput? report;

        try
        {
            report = await _catalogue.FindAsync(reportId);
        }
        catch (CatalogueException)
        {
            return Verification.CatalogueReadFailed;
        }

        if (report is null)
            return Verification.UnknownReport;

        return await CheckAsync(report);
    }

    private async Task<Verification> CheckAsync(ReportInput input)
    {
        try
        {
            var bytes = await _storage.ReadVersionAsync(
                new ExportLocation(input.Bucket, input.Key),
                input.VersionId);
            
            var sha256 = Convert.ToHexString(
                SHA256.HashData(bytes)).ToLowerInvariant();

            var rowCount = bytes.Count(b => b == (byte)'\n');
            
            return sha256 == input.Sha256 && rowCount == input.RowCount
                ? Verification.Matches
                : Verification.Mismatch;
        }
        catch (ObjectStoreException)
        {
            return Verification.ReadFailed;
        }
    }
}
