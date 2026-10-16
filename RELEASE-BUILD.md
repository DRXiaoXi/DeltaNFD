# Alpha0.9193 update-test source snapshot
This release hides all runtime UI. Build with:
powershell -NoProfile -ExecutionPolicy Bypass -File installer/publish-selfcontained.ps1 -OutDir _buildcheck/release193 -ArtifactsRoot _buildcheck/artifacts193 -RuntimeUiEnabled false
Version: Alpha0.9193. GPL-3.0-only original code; third-party licenses remain applicable.
Redistributable installer binaries are excluded from source. Prepare Microsoft originals with tools/prepare-runtime-cpp-official.py; restore reviewed auxiliary payload using tools/prepare-runtime-highfps.py and its pinned manifest. Supply the existing pinned Vc14Baseline14.42 originals. See installer/FILE-UPDATES.md for publishing.
This release retains all runtime files for a small update to Alpha0.9194; only its UI is hidden. Installing the update is manual and does not itself execute runtime repair.
