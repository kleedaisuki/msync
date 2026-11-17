bool production = args.Length > 0 && args[0] == "--production-session";
bool pendingZeroTrip = args.Length > 0 && args[0] == "--licm-pending-only";
bool prepareImage = args.Length > 0 && args[0] == "--prepare-image";
if (production || pendingZeroTrip || prepareImage) args = args[1..];
string output = args.Length == 2 && args[0] == "--output"
    ? args[1] : Path.Combine(".temp", "llvm-ir", production ? "production-session"
        : pendingZeroTrip ? "pending-zero-trip" : prepareImage ? "prepare-image" : "source-session");
if (production) await ProductionSessionJourney.RunAsync(output);
else if (pendingZeroTrip) await LicmPendingJourney.RunAsync(output);
else if (prepareImage) await PreparationImageJourney.RunAsync(output);
else await LlvmSessionJourney.RunAsync(output);
