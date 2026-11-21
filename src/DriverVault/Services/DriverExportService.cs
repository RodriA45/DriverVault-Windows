using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Text;
using DriverVault.Models;

namespace DriverVault.Services;

public sealed record ExportProgress(
    int Completed,
    int Total,
    string CurrentDriverName,
    string LogLine);

/// <summary>
/// Exporta los drivers seleccionados a sus subcarpetas, genera instalador .bat,
/// inventario HTML/TXT y archivo .zip opcional.
/// </summary>
public sealed class DriverExportService
{
    public async Task<ExtractionSummary> ExportDriversAsync(
        IReadOnlyList<DriverEntry> drivers,
        BackupOptions options,
        SystemProfile systemProfile,
        IProgress<ExportProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();

        string folderName = PathOrganizer.ResolveBackupFolderName(options.BackupName, options.IncludeDatePrefix);
        string outputRoot = PathOrganizer.ResolveUniqueOutputDirectory(
            options.DestinationRoot,
            folderName,
            options.FolderPolicy);

        Directory.CreateDirectory(outputRoot);

        var summary = new ExtractionSummary
        {
            OutputFolderPath = outputRoot
        };

        var usedFolders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int total = drivers.Count;

        for (int i = 0; i < total; i++)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                var completedItems = summary.Items.ToList();
                summary = new ExtractionSummary
                {
                    OutputFolderPath = summary.OutputFolderPath,
                    WasCancelled = true,
                    Elapsed = stopwatch.Elapsed
                };
                summary.Items.AddRange(completedItems);
                break;
            }

            DriverEntry driver = drivers[i];
            string relativeFolder = PathOrganizer.ComputeDriverRelativeFolder(driver, options.OrganizationMode, usedFolders);
            string targetSubfolder = Path.Combine(outputRoot, relativeFolder);

            progress?.Report(new ExportProgress(
                i,
                total,
                driver.PrimaryDisplayName,
                $"[{i + 1}/{total}] Exportando {driver.PrimaryDisplayName} ({driver.InfName}) -> {relativeFolder}"));

            DriverExportItemResult itemResult = await ExportSingleDriverAsync(
                driver,
                relativeFolder,
                targetSubfolder,
                cancellationToken);

            summary.Items.Add(itemResult);

            string statusTag = itemResult.Outcome switch
            {
                DriverExportOutcome.Succeeded => "OK",
                DriverExportOutcome.Skipped => "OMITIDO",
                _ => "ERROR"
            };

            progress?.Report(new ExportProgress(
                i + 1,
                total,
                driver.PrimaryDisplayName,
                $"    -> {statusTag}: {itemResult.Message}"));
        }

        if (options.GenerateInstallerScript && summary.SucceededCount > 0)
        {
            string batPath = Path.Combine(outputRoot, "Reinstalar_Drivers.bat");
            await File.WriteAllTextAsync(batPath, BuildReinstallBatchScript(systemProfile, summary), Encoding.ASCII, CancellationToken.None);
            summary = CloneWithExtras(summary, installerBatPath: batPath);
            progress?.Report(new ExportProgress(total, total, "Generando instalador", "Generado: Reinstalar_Drivers.bat"));
        }

        if (options.GenerateInventoryReport)
        {
            string txtPath = Path.Combine(outputRoot, "Inventario_Drivers.txt");
            string htmlPath = Path.Combine(outputRoot, "Inventario_Drivers.html");

            await File.WriteAllTextAsync(txtPath, BuildTextInventory(systemProfile, options, summary), Encoding.UTF8, CancellationToken.None);
            await File.WriteAllTextAsync(htmlPath, BuildHtmlInventory(systemProfile, options, summary), Encoding.UTF8, CancellationToken.None);

            summary = CloneWithExtras(
                summary,
                installerBatPath: summary.InstallerBatPath,
                reportTxtPath: txtPath,
                reportHtmlPath: htmlPath);

            progress?.Report(new ExportProgress(total, total, "Generando inventario", "Generado: Inventario_Drivers.html y Inventario_Drivers.txt"));
        }

        if (options.CompressToZip && !summary.WasCancelled)
        {
            string zipPath = $"{outputRoot}.zip";
            if (File.Exists(zipPath)) File.Delete(zipPath);

            progress?.Report(new ExportProgress(total, total, "Comprimiendo respaldo", $"Creando ZIP: {Path.GetFileName(zipPath)}..."));
            await Task.Run(() => ZipFile.CreateFromDirectory(outputRoot, zipPath, CompressionLevel.Optimal, includeBaseDirectory: true), CancellationToken.None);

            if (options.KeepZipOnly && File.Exists(zipPath))
            {
                Directory.Delete(outputRoot, recursive: true);
            }

            summary = CloneWithExtras(
                summary,
                installerBatPath: options.KeepZipOnly ? null : summary.InstallerBatPath,
                reportTxtPath: options.KeepZipOnly ? null : summary.ReportTxtPath,
                reportHtmlPath: options.KeepZipOnly ? null : summary.ReportHtmlPath,
                zipFilePath: zipPath);

            progress?.Report(new ExportProgress(total, total, "Compresión completada", $"ZIP generado: {zipPath}"));
        }

        stopwatch.Stop();
        return CloneWithElapsed(summary, stopwatch.Elapsed);
    }

    private static async Task<DriverExportItemResult> ExportSingleDriverAsync(
        DriverEntry driver,
        string relativeFolder,
        string targetSubfolder,
        CancellationToken cancellationToken)
    {
        try
        {
            Directory.CreateDirectory(targetSubfolder);

            if (driver.IsOemPackage)
            {
                string pnpUtilExe = Path.Combine(Environment.SystemDirectory, "pnputil.exe");
                if (!File.Exists(pnpUtilExe)) pnpUtilExe = "pnputil.exe";

                var psi = new ProcessStartInfo
                {
                    FileName = pnpUtilExe,
                    Arguments = $"/export-driver \"{driver.InfName}\" \"{targetSubfolder}\"",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };

                using var proc = new Process { StartInfo = psi };
                proc.Start();

                Task<string> stdOutTask = proc.StandardOutput.ReadToEndAsync(cancellationToken);
                Task<string> stdErrTask = proc.StandardError.ReadToEndAsync(cancellationToken);

                await proc.WaitForExitAsync(cancellationToken);
                string stdOut = (await stdOutTask).Trim();
                string stdErr = (await stdErrTask).Trim();

                long written = CalculateFolderBytes(targetSubfolder);

                if (proc.ExitCode == 0 && written > 0)
                {
                    return new DriverExportItemResult
                    {
                        Driver = driver,
                        Outcome = DriverExportOutcome.Succeeded,
                        TargetRelativeFolder = relativeFolder,
                        BytesWritten = written,
                        Message = $"Exportado ({DriverEntry.FormatBytes(written)})"
                    };
                }

                // Fallback si pnputil falló pero existe DriverStorePath legible
                if (!string.IsNullOrWhiteSpace(driver.DriverStorePath) && Directory.Exists(driver.DriverStorePath))
                {
                    CopyDirectory(driver.DriverStorePath, targetSubfolder);
                    written = CalculateFolderBytes(targetSubfolder);
                    if (written > 0)
                    {
                        return new DriverExportItemResult
                        {
                            Driver = driver,
                            Outcome = DriverExportOutcome.Succeeded,
                            TargetRelativeFolder = relativeFolder,
                            BytesWritten = written,
                            Message = $"Copiado desde DriverStore ({DriverEntry.FormatBytes(written)})"
                        };
                    }
                }

                TryDeleteEmptyDirectory(targetSubfolder);
                string errorDetail = FirstNonEmpty(stdErr, stdOut, $"Código de salida {proc.ExitCode}");
                return new DriverExportItemResult
                {
                    Driver = driver,
                    Outcome = DriverExportOutcome.Failed,
                    TargetRelativeFolder = relativeFolder,
                    Message = errorDetail
                };
            }

            // Paquete inbox de Windows: copiar carpeta DriverStore si está disponible
            if (!string.IsNullOrWhiteSpace(driver.DriverStorePath) && Directory.Exists(driver.DriverStorePath))
            {
                await Task.Run(() => CopyDirectory(driver.DriverStorePath, targetSubfolder), cancellationToken);
                long written = CalculateFolderBytes(targetSubfolder);
                return new DriverExportItemResult
                {
                    Driver = driver,
                    Outcome = DriverExportOutcome.Succeeded,
                    TargetRelativeFolder = relativeFolder,
                    BytesWritten = written,
                    Message = $"Copiado desde DriverStore ({DriverEntry.FormatBytes(written)})"
                };
            }

            TryDeleteEmptyDirectory(targetSubfolder);
            return new DriverExportItemResult
            {
                Driver = driver,
                Outcome = DriverExportOutcome.Skipped,
                TargetRelativeFolder = relativeFolder,
                Message = "Driver integrado de Windows sin carpeta exportable."
            };
        }
        catch (OperationCanceledException)
        {
            TryDeleteEmptyDirectory(targetSubfolder);
            return new DriverExportItemResult
            {
                Driver = driver,
                Outcome = DriverExportOutcome.Skipped,
                TargetRelativeFolder = relativeFolder,
                Message = "Cancelado por el usuario."
            };
        }
        catch (Exception ex)
        {
            TryDeleteEmptyDirectory(targetSubfolder);
            return new DriverExportItemResult
            {
                Driver = driver,
                Outcome = DriverExportOutcome.Failed,
                TargetRelativeFolder = relativeFolder,
                Message = ex.Message
            };
        }
    }

    public static string BuildReinstallBatchScript(SystemProfile profile, ExtractionSummary summary)
    {
        var sb = new StringBuilder();
        sb.AppendLine("@echo off");
        sb.AppendLine("chcp 65001 >nul");
        sb.AppendLine($"title Reinstalar Drivers - {profile.MachineName}");
        sb.AppendLine("echo ================================================================");
        sb.AppendLine($"echo   DriverVault - Instalador automatico de drivers respaldados");
        sb.AppendLine($"echo   Equipo original: {profile.MachineName} ({profile.ManufacturerModel})");
        sb.AppendLine($"echo   Drivers incluidos: {summary.SucceededCount}");
        sb.AppendLine("echo ================================================================");
        sb.AppendLine("echo.");
        sb.AppendLine("net session >nul 2>&1");
        sb.AppendLine("if %errorLevel% neq 0 (");
        sb.AppendLine("    echo [AVISO] Necesitas ejecutar este archivo como ADMINISTRADOR.");
        sb.AppendLine("    echo Haz clic derecho sobre Reinstalar_Drivers.bat ^> Ejecutar como administrador.");
        sb.AppendLine("    pause");
        sb.AppendLine("    exit /b 1");
        sb.AppendLine(")");
        sb.AppendLine("echo ATENCION: Se instalaran todos los controladores .inf de las subcarpetas.");
        sb.AppendLine("echo Presiona cualquier tecla para comenzar o cierra esta ventana para cancelar...");
        sb.AppendLine("pause >nul");
        sb.AppendLine("echo.");
        sb.AppendLine("pnputil /add-driver \"%~dp0*.inf\" /subdirs /install");
        sb.AppendLine("echo.");
        sb.AppendLine("echo ================================================================");
        sb.AppendLine("echo   Proceso finalizado. Si algun driver lo solicita, reinicia la PC.");
        sb.AppendLine("echo ================================================================");
        sb.AppendLine("pause");
        return sb.ToString();
    }

    public static string BuildTextInventory(SystemProfile profile, BackupOptions options, ExtractionSummary summary)
    {
        var sb = new StringBuilder();
        sb.AppendLine("========================================================================");
        sb.AppendLine(" DRIVERVAULT - INVENTARIO DE DRIVERS RESPALDADOS");
        sb.AppendLine("========================================================================");
        sb.AppendLine($"Fecha de respaldo : {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine($"Equipo            : {profile.MachineName} ({profile.ManufacturerModel})");
        sb.AppendLine($"Sistema Operativo : {profile.OsCaption} ({profile.OsVersion}) - {profile.Architecture}");
        sb.AppendLine($"RAM               : {profile.TotalRamFormatted}");
        sb.AppendLine($"Nombre respaldo   : {options.BackupName}");
        sb.AppendLine($"Carpeta destino   : {summary.OutputFolderPath}");
        sb.AppendLine($"Exitosos / Total  : {summary.SucceededCount} / {summary.Items.Count} ({DriverEntry.FormatBytes(summary.TotalBytesWritten)})");
        sb.AppendLine("------------------------------------------------------------------------");
        sb.AppendLine();

        foreach (DriverExportItemResult item in summary.Items)
        {
            string status = item.Outcome switch
            {
                DriverExportOutcome.Succeeded => "[OK]",
                DriverExportOutcome.Skipped => "[OMITIDO]",
                _ => "[ERROR]"
            };
            sb.AppendLine($"{status} {item.Driver.PrimaryDisplayName}");
            sb.AppendLine($"    Proveedor : {item.Driver.Provider} | Categoria: {item.Driver.CategoryFolder}");
            sb.AppendLine($"    Version   : {item.Driver.Version} | Fecha: {item.Driver.DriverDate} | INF: {item.Driver.InfName} ({item.Driver.OriginalInfName})");
            sb.AppendLine($"    Subcarpeta: {item.TargetRelativeFolder}");
            sb.AppendLine($"    Resultado : {item.Message}");
            sb.AppendLine();
        }

        return sb.ToString();
    }

    public static string BuildHtmlInventory(SystemProfile profile, BackupOptions options, ExtractionSummary summary)
    {
        var rows = new StringBuilder();
        foreach (DriverExportItemResult item in summary.Items)
        {
            string badge = item.Outcome switch
            {
                DriverExportOutcome.Succeeded => "<span class='badge ok'>OK</span>",
                DriverExportOutcome.Skipped => "<span class='badge'>OMITIDO</span>",
                _ => "<span class='badge err'>ERROR</span>"
            };

            rows.AppendLine($"""
                <tr>
                    <td>{badge}</td>
                    <td><strong>{WebUtility.HtmlEncode(item.Driver.PrimaryDisplayName)}</strong><br><small>{WebUtility.HtmlEncode(item.Driver.InfName)} ({WebUtility.HtmlEncode(item.Driver.OriginalInfName)})</small></td>
                    <td>{WebUtility.HtmlEncode(item.Driver.CategoryFolder)}</td>
                    <td>{WebUtility.HtmlEncode(item.Driver.Provider)}</td>
                    <td class='mono'>{WebUtility.HtmlEncode(item.Driver.Version)}</td>
                    <td class='mono'>{WebUtility.HtmlEncode(item.Driver.DriverDate)}</td>
                    <td class='mono'>{WebUtility.HtmlEncode(item.TargetRelativeFolder)}</td>
                    <td class='mono'>{WebUtility.HtmlEncode(DriverEntry.FormatBytes(item.BytesWritten))}</td>
                </tr>
                """);
        }

        return $$"""
        <!DOCTYPE html>
        <html lang="es">
        <head>
            <meta charset="utf-8" />
            <title>Inventario de Drivers - {{WebUtility.HtmlEncode(profile.MachineName)}}</title>
            <style>
                body { font-family: -apple-system, BlinkMacSystemFont, "Segoe UI", sans-serif; background: #0a0a0a; color: #f5f5f5; margin: 0; padding: 32px; }
                .wrap { max-width: 1200px; margin: 0 auto; }
                header { border-bottom: 1px solid #2b2b2b; padding-bottom: 20px; margin-bottom: 24px; display: flex; justify-content: space-between; align-items: flex-end; flex-wrap: wrap; gap: 16px; }
                h1 { margin: 0 0 6px 0; font-size: 24px; letter-spacing: -0.02em; }
                .sub { color: #a3a3a3; font-size: 13px; }
                .grid { display: grid; grid-template-columns: repeat(auto-fit, minmax(210px, 1fr)); gap: 12px; margin-bottom: 24px; }
                .card { background: #141414; border: 1px solid #262626; border-radius: 10px; padding: 14px 16px; }
                .label { font-size: 11px; text-transform: uppercase; letter-spacing: 0.08em; color: #8c8c8c; margin-bottom: 6px; }
                .val { font-size: 16px; font-weight: 600; }
                table { width: 100%; border-collapse: collapse; background: #141414; border: 1px solid #262626; border-radius: 10px; overflow: hidden; font-size: 13px; }
                th, td { text-align: left; padding: 11px 12px; border-bottom: 1px solid #222; vertical-align: top; }
                th { background: #1a1a1a; font-size: 11px; text-transform: uppercase; letter-spacing: 0.07em; color: #b5b5b5; }
                .mono { font-family: Consolas, "Courier New", monospace; font-size: 12px; }
                small { color: #8c8c8c; }
                .badge { display: inline-block; padding: 2px 8px; border-radius: 999px; border: 1px solid #555; font-size: 11px; font-weight: 600; }
                .badge.ok { background: #fff; color: #0a0a0a; border-color: #fff; }
                .badge.err { border-style: dashed; color: #fff; }
                @media print { body { background: #fff; color: #000; } .card, table, th { background: #fff; color: #000; border-color: #ccc; } .badge.ok { background: #000; color: #fff; } }
            </style>
        </head>
        <body>
            <div class="wrap">
                <header>
                    <div>
                        <h1>DriverVault · Inventario de Drivers</h1>
                        <div class="sub">Generado el {{DateTime.Now:yyyy-MM-dd HH:mm:ss}} · Respaldo: {{WebUtility.HtmlEncode(options.BackupName)}}</div>
                    </div>
                    <div class="sub">{{WebUtility.HtmlEncode(profile.OsCaption)}} ({{WebUtility.HtmlEncode(profile.Architecture)}})</div>
                </header>
                <div class="grid">
                    <div class="card"><div class="label">Equipo</div><div class="val">{{WebUtility.HtmlEncode(profile.MachineName)}} · {{WebUtility.HtmlEncode(profile.ManufacturerModel)}}</div></div>
                    <div class="card"><div class="label">Exportados con éxito</div><div class="val">{{summary.SucceededCount}} de {{summary.Items.Count}}</div></div>
                    <div class="card"><div class="label">Tamaño total</div><div class="val">{{DriverEntry.FormatBytes(summary.TotalBytesWritten)}}</div></div>
                    <div class="card"><div class="label">Carpeta destino</div><div class="val" style="font-size:12px;word-break:break-all;">{{WebUtility.HtmlEncode(summary.OutputFolderPath)}}</div></div>
                </div>
                <table>
                    <thead>
                        <tr>
                            <th>Estado</th>
                            <th>Dispositivo / INF</th>
                            <th>Categoría</th>
                            <th>Proveedor</th>
                            <th>Versión</th>
                            <th>Fecha</th>
                            <th>Subcarpeta</th>
                            <th>Tamaño</th>
                        </tr>
                    </thead>
                    <tbody>
                        {{rows}}
                    </tbody>
                </table>
            </div>
        </body>
        </html>
        """;
    }

    private static void CopyDirectory(string sourceDir, string destDir)
    {
        Directory.CreateDirectory(destDir);
        foreach (string file in Directory.GetFiles(sourceDir))
        {
            string destFile = Path.Combine(destDir, Path.GetFileName(file));
            File.Copy(file, destFile, overwrite: true);
        }

        foreach (string subDir in Directory.GetDirectories(sourceDir))
        {
            string destSubDir = Path.Combine(destDir, Path.GetFileName(subDir));
            CopyDirectory(subDir, destSubDir);
        }
    }

    private static long CalculateFolderBytes(string folder)
    {
        if (!Directory.Exists(folder)) return 0;
        try
        {
            return new DirectoryInfo(folder).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length);
        }
        catch
        {
            return 0;
        }
    }

    private static void TryDeleteEmptyDirectory(string dir)
    {
        try
        {
            if (Directory.Exists(dir) && !Directory.EnumerateFileSystemEntries(dir).Any())
            {
                Directory.Delete(dir);
            }
        }
        catch
        {
            // Ignorar si está en uso
        }
    }

    private static string FirstNonEmpty(params string?[] candidates)
    {
        foreach (string? c in candidates)
        {
            if (!string.IsNullOrWhiteSpace(c)) return c;
        }
        return string.Empty;
    }

    private static ExtractionSummary CloneWithExtras(
        ExtractionSummary source,
        string? installerBatPath = null,
        string? reportTxtPath = null,
        string? reportHtmlPath = null,
        string? zipFilePath = null)
    {
        var copy = new ExtractionSummary
        {
            OutputFolderPath = source.OutputFolderPath,
            InstallerBatPath = installerBatPath ?? source.InstallerBatPath,
            ReportTxtPath = reportTxtPath ?? source.ReportTxtPath,
            ReportHtmlPath = reportHtmlPath ?? source.ReportHtmlPath,
            ZipFilePath = zipFilePath ?? source.ZipFilePath,
            WasCancelled = source.WasCancelled,
            Elapsed = source.Elapsed
        };
        copy.Items.AddRange(source.Items);
        return copy;
    }

    private static ExtractionSummary CloneWithElapsed(ExtractionSummary source, TimeSpan elapsed)
    {
        var copy = new ExtractionSummary
        {
            OutputFolderPath = source.OutputFolderPath,
            InstallerBatPath = source.InstallerBatPath,
            ReportTxtPath = source.ReportTxtPath,
            ReportHtmlPath = source.ReportHtmlPath,
            ZipFilePath = source.ZipFilePath,
            WasCancelled = source.WasCancelled,
            Elapsed = elapsed
        };
        copy.Items.AddRange(source.Items);
        return copy;
    }
}
