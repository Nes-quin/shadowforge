using System.CommandLine;
using Microsoft.Extensions.Logging;
using ShadowForge.Formats.HMB;
using ShadowForge.Formats.HMB.Import;
using ShadowForge.GameData;

namespace ShadowForge.CLI.Commands;

public static class MOTCommands
{
    public static Command Create(Func<ParseResult, ILoggerFactory> getFactory)
    {
        var cmd = new Command("mot", "HMB motion clip operations.");
        cmd.Subcommands.Add(BuildInspectCommand(getFactory));
        cmd.Subcommands.Add(BuildImportCommand(getFactory));
        return cmd;
    }

    private static Command BuildInspectCommand(Func<ParseResult, ILoggerFactory> getFactory)
    {
        var fileArg = new Argument<string>("file-or-id") { Description = "mot.mpk or .hmb file, or an entity id (pc01)." };
        var tracksOpt = new Option<bool>("--tracks") { Description = "Also print each track's bone name and channel key counts." };
        var rootOpt = new GameRootOption();
        var cmd = new Command("inspect", "Dump parsed HMB motion clips.");
        cmd.Arguments.Add(fileArg);
        cmd.Options.Add(tracksOpt);
        cmd.Options.Add(rootOpt);
        cmd.SetAction(pr =>
        {
            var log = getFactory(pr).CreateLogger("mot.inspect");
            string arg = pr.GetValue(fileArg)!;
            bool showTracks = pr.GetValue(tracksOpt);
            try
            {
                using var input = EntityArg.File(pr.GetValue(rootOpt), arg, FileRole.Motion, withSiblings: false);
                var file = new FileInfo(input.Path);
                List<MotionClip> clips;
                List<string> warnings;
                if (file.Extension.Equals(".mpk", StringComparison.OrdinalIgnoreCase))
                {
                    clips = MotPack.ReadAll(file.FullName, out warnings);
                }
                else
                {
                    var clip = MotionReader.Read(File.ReadAllBytes(file.FullName), Path.GetFileNameWithoutExtension(file.Name));
                    clips = [clip];
                    warnings = [];
                    if (clip.ExtraSectionTypes.Count > 0)
                        warnings.Add($"{file.Name}: extra FT sections " +
                            string.Join(", ", clip.ExtraSectionTypes.Select(t => $"0x{t:X}")));
                }

                foreach (var clip in clips)
                {
                    Console.WriteLine(
                        $"{clip.Name} mode={clip.Mode} dur={clip.DurationFrames} " +
                        $"rate={clip.Rate}/{clip.RateFlag} tracks={clip.Tracks.Count}");
                    if (!showTracks) continue;
                    foreach (var track in clip.Tracks)
                    {
                        Console.WriteLine(
                            $"  {track.BoneName} T:{FormatChannel(track.Translation?.Linear, track.Translation?.Axes)} " +
                            $"R:{FormatChannel(track.Rotation?.Linear, track.Rotation?.Axes)} " +
                            $"S:{FormatChannel(track.Scale?.Linear, track.Scale?.Axes)}");
                    }
                }
                foreach (var warning in warnings)
                    Console.WriteLine($"warning: {warning}");
                return 0;
            }
            catch (Exception ex)
            {
                log.LogError(ex, "Failed to inspect {File}", arg);
                return 1;
            }
        });
        return cmd;
    }

    private static Command BuildImportCommand(Func<ParseResult, ILoggerFactory> getFactory)
    {
        var glbArg = new Argument<FileInfo>("glb") { Description = "GLTF or GLB file containing baked animation clips." };
        var mpkOpt = new Option<string?>("--mpk")
            { Description = "Template mot.mpk path or entity id (default: a new pack holding only the imported clips)." };
        var outputOpt = new Option<FileInfo?>("-o")
            { Description = "Output .mpk path (default: <template>.new.mpk beside the template, or <glb>_mot.mpk beside the glb)." };
        var hdbOpt = new Option<string?>("--hdb")
            { Description = "Model HDB path or entity id, required for bone names, frames and orient factors." };
        var rootOpt = new GameRootOption();
        var cmd = new Command("import", "Import GLTF animations into a repacked mot.mpk.");
        cmd.Arguments.Add(glbArg);
        cmd.Options.Add(mpkOpt);
        cmd.Options.Add(outputOpt);
        cmd.Options.Add(hdbOpt);
        cmd.Options.Add(rootOpt);
        cmd.SetAction(pr =>
        {
            var log = getFactory(pr).CreateLogger("mot.import");
            var glbFile = pr.GetValue(glbArg)!;
            string? hdbArg = pr.GetValue(hdbOpt);
            string? mpkArg = pr.GetValue(mpkOpt);
            if (string.IsNullOrWhiteSpace(hdbArg))
            {
                log.LogError(
                    "--hdb <model.hdb|id> is required, it supplies the bone ExtraEuler orient factors removed from "
                    + "imported rotation tracks and the bone names every track is checked against.");
                return 1;
            }

            try
            {
                string? gameRoot = pr.GetValue(rootOpt);
                using var hdbInput = EntityArg.File(gameRoot, hdbArg, FileRole.Skeleton, withSiblings: false);
                using var mpkInput = string.IsNullOrWhiteSpace(mpkArg)
                    ? null
                    : EntityArg.File(gameRoot, mpkArg, FileRole.Motion, withSiblings: false);

                string output = pr.GetValue(outputOpt)?.FullName ?? DefaultOutput(mpkInput, glbFile);
                if (mpkInput is not null
                    && Path.GetFullPath(output).Equals(Path.GetFullPath(mpkInput.Path), StringComparison.OrdinalIgnoreCase))
                {
                    log.LogError("Output would overwrite the template mpk, choose a different -o.");
                    return 1;
                }

                using var mpkStream = mpkInput is null ? null : File.OpenRead(mpkInput.Path);
                var result = MotionImporter.Import(glbFile.FullName, File.ReadAllBytes(hdbInput.Path), mpkStream);
                foreach (var w in result.Warnings) log.LogWarning("{Warning}", w);

                OutputPath.CreateParentDirectory(output);
                File.WriteAllBytes(output, result.MPKBytes);
                Console.WriteLine($"{result.Replaced} replaced, {result.Appended} appended, {result.Untouched} untouched.");
                Console.WriteLine($"Wrote {result.MPKBytes.Length:N0} bytes to {Path.GetFullPath(output)}");
                return 0;
            }
            catch (Exception ex)
            {
                log.LogError(ex, "Import failed for {File}", glbFile.FullName);
                return 1;
            }
        });
        return cmd;
    }

    private static string DefaultOutput(ResolvedArg? template, FileInfo glbFile) =>
        template?.DefaultOutput(".new.mpk")
        ?? Path.Combine(glbFile.DirectoryName ?? ".", Path.GetFileNameWithoutExtension(glbFile.Name) + "_mot.mpk");

    private static string FormatChannel(Array? linear, HermiteCurve[]? axes) =>
        linear is not null ? $"{linear.Length}"
        : axes is not null ? $"hermite({axes[0].Keys.Length},{axes[1].Keys.Length},{axes[2].Keys.Length})"
        : "-";
}
