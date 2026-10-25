using System.CommandLine;
using Microsoft.Extensions.Logging;
using ShadowForge.Formats.DDS;
using ShadowForge.Formats.GLTF;
using ShadowForge.Formats.HDB;
using ShadowForge.Formats.HDB.Import;
using ShadowForge.Formats.HMB;
using ShadowForge.GameData;

namespace ShadowForge.CLI.Commands;

public static class HDBCommands
{
    public static Command Create(Func<ParseResult, ILoggerFactory> getFactory)
    {
        var cmd = new Command("hdb", "HDB model operations.");
        cmd.Subcommands.Add(BuildExportCommand(getFactory));
        cmd.Subcommands.Add(BuildBatchExportCommand(getFactory));
        cmd.Subcommands.Add(BuildImportCommand(getFactory));
        cmd.Subcommands.Add(BuildInspectCommand(getFactory));
        cmd.Subcommands.Add(BuildDiffBonesCommand());
        cmd.Subcommands.Add(BuildValidateCommand());
        cmd.Subcommands.Add(BuildGeoCheckCommand());
        return cmd;
    }

    private static Command BuildExportCommand(Func<ParseResult, ILoggerFactory> getFactory)
    {
        var fileArg = new Argument<string>("file-or-id") { Description = "HDB model file, or an entity id (pc01)." };
        var outputOpt = new Option<string?>("-o") { Description = "Output .glb file, or a directory to write <name>.glb into." };
        var texturesOpt = new Option<DirectoryInfo?>("--textures")
            { Description = "Directory to search for DDS and 36T textures (default: the HDB file's directory)." };
        var formatOpt = new Option<string?>("--format")
            { Description = "glb (self-contained) or gltf (external textures), defaulting to the -o extension, then glb." };
        var motOpt = new Option<string?>("--mot")
            { Description = "Animation mpk path, 'auto' for the sibling *_mot.mpk (default), or 'none'." };
        var rootOpt = new GameRootOption();
        var cmd = new Command("export", "Export an HDB model to GLTF (glb or gltf).");
        cmd.Arguments.Add(fileArg);
        cmd.Options.Add(outputOpt);
        cmd.Options.Add(texturesOpt);
        cmd.Options.Add(formatOpt);
        cmd.Options.Add(motOpt);
        cmd.Options.Add(rootOpt);
        cmd.SetAction(pr =>
        {
            var log = getFactory(pr).CreateLogger("hdb.export");
            string arg = pr.GetValue(fileArg)!;
            string? oValue = pr.GetValue(outputOpt);
            string? ext = GLTFFormat.Resolve(pr.GetValue(formatOpt), oValue);
            if (ext is null)
            {
                log.LogError("Unknown --format '{Fmt}'. {Supported}", pr.GetValue(formatOpt), GLTFFormat.Supported);
                return 1;
            }

            try
            {
                using var input = EntityArg.File(pr.GetValue(rootOpt), arg, FileRole.Skeleton, withSiblings: true);
                string output = string.IsNullOrEmpty(oValue)
                    ? input.DefaultOutput(ext)
                    : OutputPath.Resolve(oValue, input.Stem, ext);
                string textureDir = pr.GetValue(texturesOpt)?.FullName ?? input.Dir;

                log.LogInformation("Reading {File}", Path.GetFileName(input.Path));
                byte[] hdbBytes = File.ReadAllBytes(input.Path);
                var clips = ReadClips(pr.GetValue(motOpt), input.Path, log);

                log.LogInformation("Exporting to {Output}", Path.GetFileName(output));
                OutputPath.CreateParentDirectory(output);
                foreach (var w in ModelExporter.Export(hdbBytes, output, embed: ext == ".glb", clips, textureDir: textureDir))
                    log.LogWarning("{Warning}", w);
                Console.WriteLine($"Done. {new FileInfo(output).Length:N0} bytes written.");
                return 0;
            }
            catch (Exception ex)
            {
                log.LogError(ex, "Export failed for {File}", arg);
                return 1;
            }
        });
        return cmd;
    }

    /// <summary>
    /// Reads the clips named by --mot. "auto" or no value looks for {stem}_mot.mpk beside the
    /// HDB, with a trailing _obj dropped from the stem, and returns null when it is absent.
    /// </summary>
    private static List<MotionClip>? ReadClips(string? motArg, string hdbPath, ILogger log)
    {
        if (string.Equals(motArg, "none", StringComparison.OrdinalIgnoreCase))
            return null;

        string mpkPath;
        if (!string.IsNullOrEmpty(motArg) && !string.Equals(motArg, "auto", StringComparison.OrdinalIgnoreCase))
        {
            mpkPath = motArg;
        }
        else
        {
            string stem = Path.GetFileNameWithoutExtension(hdbPath);
            if (stem.EndsWith("_obj", StringComparison.OrdinalIgnoreCase))
                stem = stem[..^"_obj".Length];
            mpkPath = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(hdbPath)) ?? "", stem + "_mot.mpk");
            if (!File.Exists(mpkPath))
            {
                log.LogInformation("No sibling animation pack at {Path}, exporting without clips.", mpkPath);
                return null;
            }
        }

        var clips = MotPack.ReadAll(mpkPath, out var warnings);
        foreach (var w in warnings)
            log.LogWarning("{Warning}", w);
        log.LogInformation("Loaded {Count} clips from {Path}", clips.Count, mpkPath);
        return clips;
    }

    private static Command BuildBatchExportCommand(Func<ParseResult, ILoggerFactory> getFactory)
    {
        var dirArg = new Argument<DirectoryInfo>("directory") { Description = "Directory containing HDB files." };
        var outputOpt = new Option<DirectoryInfo?>("-o") { Description = "Output directory." };
        var texturesOpt = new Option<DirectoryInfo?>("--textures")
            { Description = "Directory to search for DDS and 36T textures (default: each HDB file's directory)." };
        var formatOpt = new Option<string?>("--format")
            { Description = "glb (default, self-contained) or gltf (external textures)." };
        var cmd = new Command("batch-export", "Export every HDB file in a directory to GLTF.");
        cmd.Arguments.Add(dirArg);
        cmd.Options.Add(outputOpt);
        cmd.Options.Add(texturesOpt);
        cmd.Options.Add(formatOpt);
        cmd.SetAction(pr =>
        {
            var log = getFactory(pr).CreateLogger("hdb.batch-export");
            var dir = pr.GetValue(dirArg)!;
            var outputDir = pr.GetValue(outputOpt) ?? dir;
            string? ext = GLTFFormat.Resolve(pr.GetValue(formatOpt));
            if (ext is null)
            {
                log.LogError("Unknown --format '{Fmt}'. {Supported}", pr.GetValue(formatOpt), GLTFFormat.Supported);
                return 1;
            }
            outputDir.Create();
            string? textureDir = pr.GetValue(texturesOpt)?.FullName;
            return BatchConvert.Run(BatchConvert.Find(dir, "*.hdb"), "exported", log, file =>
            {
                string outPath = Path.Combine(outputDir.FullName, Path.ChangeExtension(Path.GetFileName(file), ext));
                var warnings = ModelExporter.Export(File.ReadAllBytes(file), outPath, embed: ext == ".glb",
                    textureDir: textureDir ?? Path.GetDirectoryName(file));
                foreach (var w in warnings)
                    log.LogWarning("{Warning}", w);
                return null;
            });
        });
        return cmd;
    }

    private static Command BuildImportCommand(Func<ParseResult, ILoggerFactory> getFactory)
    {
        var fileArg = new Argument<FileInfo>("file") { Description = "GLTF or GLB input file." };
        var outputOpt = new Option<FileInfo?>("-o") { Description = "Output .hdb file (default: <input>.hdb beside the input)." };
        var formatOpt = new Option<string?>("--format")
            { Description = "DDS format override such as DXT1 or DXT5 (default: chosen by alpha presence)." };
        var rigOpt = new Option<string?>("--rig")
            { Description = "Rig HDB path or entity id whose bone frames and extra euler are kept, so its cutscene and face motions still fit (default: none)." };
        var rootOpt = new GameRootOption();
        var cmd = new Command("import", "Import GLTF to HDB and DDS.");
        cmd.Arguments.Add(fileArg);
        cmd.Options.Add(outputOpt);
        cmd.Options.Add(formatOpt);
        cmd.Options.Add(rigOpt);
        cmd.Options.Add(rootOpt);
        cmd.SetAction(pr =>
        {
            var log = getFactory(pr).CreateLogger("hdb.import");
            var file = pr.GetValue(fileArg)!;
            string? formatName = pr.GetValue(formatOpt);
            string? rigArg = pr.GetValue(rigOpt);
            try
            {
                GraphicFormat? formatOverride = string.IsNullOrEmpty(formatName) ? null : GraphicFormatParser.Parse(formatName);
                string output = OutputPath.ForFile(pr.GetValue(outputOpt), file.FullName, ".hdb");
                IReadOnlyList<Bone>? rigBones = null;
                if (!string.IsNullOrWhiteSpace(rigArg))
                {
                    using var rigInput = EntityArg.File(pr.GetValue(rootOpt), rigArg, FileRole.Skeleton, withSiblings: false);
                    rigBones = ModelCooker.Bake(ModelReader.Read(rigInput.Path)).Bones;
                }
                ModelImporter.Import(file.FullName, output, log, formatOverride, rigBones);
                return 0;
            }
            catch (Exception ex)
            {
                log.LogError(ex, "Import failed for {File}", file.FullName);
                return 1;
            }
        });
        return cmd;
    }

    private static Command BuildInspectCommand(Func<ParseResult, ILoggerFactory> getFactory)
    {
        var fileArg = new Argument<string>("file-or-id") { Description = "HDB file, or an entity id (pc01)." };
        var rootOpt = new GameRootOption();
        var cmd = new Command("inspect", "Dump the parsed HDB structure in build emit format.");
        cmd.Arguments.Add(fileArg);
        cmd.Options.Add(rootOpt);
        cmd.SetAction(pr =>
        {
            var log = getFactory(pr).CreateLogger("hdb.inspect");
            string arg = pr.GetValue(fileArg)!;
            try
            {
                using var input = EntityArg.File(pr.GetValue(rootOpt), arg, FileRole.Skeleton, withSiblings: false);
                Inspector.Dump(ModelReader.Read(File.ReadAllBytes(input.Path)), log);
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

    private static Command BuildDiffBonesCommand()
    {
        var leftArg = new Argument<FileInfo>("left") { Description = "First HDB." };
        var rightArg = new Argument<FileInfo>("right") { Description = "Second HDB." };
        var thresholdOpt = new Option<double>("--threshold")
        {
            Description = "Per-element difference above which a bone is listed.",
            DefaultValueFactory = _ => 1e-3,
        };
        var cmd = new Command("diff-bones", "Report bones whose bind-pose world matrix or position differs between two HDB files.");
        cmd.Arguments.Add(leftArg);
        cmd.Arguments.Add(rightArg);
        cmd.Options.Add(thresholdOpt);
        cmd.SetAction(pr =>
        {
            var l = ModelCooker.Bake(ModelReader.Read(File.ReadAllBytes(pr.GetValue(leftArg)!.FullName)));
            var r = ModelCooker.Bake(ModelReader.Read(File.ReadAllBytes(pr.GetValue(rightArg)!.FullName)));
            double thr = pr.GetValue(thresholdOpt);

            var lG = BindPose.ComputeGlobals(l.Bones);
            var rG = BindPose.ComputeGlobals(r.Bones);

            var rByName = new Dictionary<string, (Bone Bone, BoneGlobal Global)>();
            for (int i = 0; i < r.Bones.Count; i++)
            {
                if (rG[i] != null) rByName[r.Bones[i].Name] = (r.Bones[i], rG[i]);
            }

            int matched = 0, diffCount = 0;
            Console.WriteLine($"{"Bone",-22} {"side",-5} {"HFlag",-10} {"posDiff",-10} {"matMaxDiff",-12} {"L.scale",-22} {"R.scale",-22}");
            for (int i = 0; i < l.Bones.Count; i++)
            {
                var lb = l.Bones[i];
                var lg = lG[i];
                if (lg == null || !rByName.TryGetValue(lb.Name, out var rp)) continue;
                matched++;
                var (rb, rg) = rp;

                double posDiff = (lg.Position - rg.Position).Length();
                double matMax = 0;
                for (int a = 0; a < 3; a++)
                {
                    for (int b = 0; b < 3; b++)
                        matMax = Math.Max(matMax, Math.Abs(lg.Matrix[a, b] - rg.Matrix[a, b]));
                }

                if (posDiff > thr || matMax > thr)
                {
                    diffCount++;
                    Console.WriteLine(
                        $"{lb.Name,-22} {"L",-5} 0x{lb.HFlag:X8} {posDiff,10:F4} {matMax,12:F4} " +
                        $"({lb.ScaleX:F3},{lb.ScaleY:F3},{lb.ScaleZ:F3})  ({rb.ScaleX:F3},{rb.ScaleY:F3},{rb.ScaleZ:F3})");
                }
            }
            Console.WriteLine($"\n{matched} bones matched by name, {diffCount} differ above threshold {thr:G}.");
            return 0;
        });
        return cmd;
    }

    private static Command BuildValidateCommand()
    {
        var fileArg = new Argument<FileInfo[]>("files") { Description = "HDB files to validate." };
        var dumpOpt = new Option<bool>("--dump") { Description = "Print the structural dump alongside findings." };
        var cmd = new Command("validate",
            "Replay the runtime load path (header, rebase, scene walk, render commands, draw bounds) and report every violation.");
        cmd.Arguments.Add(fileArg);
        cmd.Options.Add(dumpOpt);
        cmd.SetAction(pr =>
        {
            bool dump = pr.GetValue(dumpOpt);
            int exitCode = 0;
            foreach (var file in pr.GetValue(fileArg)!)
            {
                var v = ModelValidator.Run(File.ReadAllBytes(file.FullName));
                int errors = v.Findings.Count(f => f.Level == ModelValidator.Level.Error);
                int warns = v.Findings.Count(f => f.Level == ModelValidator.Level.Warning);
                Console.WriteLine($"{file.Name}: {(errors == 0 ? "OK" : "FAIL")} ({errors} errors, {warns} warnings)");
                foreach (var f in v.Findings)
                    Console.WriteLine($"  [{f.Level}] {f.Message}");
                if (dump) Console.Write(v.Dump.ToString());
                if (errors > 0) exitCode = 1;
            }
            return exitCode;
        });
        return cmd;
    }

    private static Command BuildGeoCheckCommand()
    {
        var fileArg = new Argument<FileInfo[]>("files") { Description = "HDB files to check." };
        var cmd = new Command("geocheck",
            "Skin every vertex through each of its bone influences with the runtime matrix math and report divergence.");
        cmd.Arguments.Add(fileArg);
        cmd.SetAction(pr =>
        {
            int exitCode = 0;
            foreach (var file in pr.GetValue(fileArg)!)
            {
                var res = GeometryCheck.Run(File.ReadAllBytes(file.FullName));
                Console.WriteLine($"{file.Name}:");
                Console.Write(res.Log.ToString());
                foreach (var problem in res.Problems) Console.WriteLine($"  [problem] {problem}");
                if (res.Problems.Count > 0) exitCode = 1;
            }
            return exitCode;
        });
        return cmd;
    }
}
