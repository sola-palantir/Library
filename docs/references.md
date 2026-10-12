# Implementation references

Reviewed 2026-10-12. Package versions were also resolved from the live npm and NuGet registries during the local build.

- [.NET support policy](https://dotnet.microsoft.com/en-us/platform/support/policy/dotnet-core): .NET 10 LTS supported through November 2028. The service uses the current resolved 10.0.401 SDK and 10.0.12 packages/runtime.
- [React versions](https://react.dev/versions): React 19.3.0 was the current documented release.
- [Vite getting started](https://vite.dev/guide/): Node 24 is used for the build toolchain.
- [Plex NFO metadata files](https://support.plex.tv/articles/using-nfo-metadata-files-with-plex/): referenced from the approved research brief; the browsing tool returned an error for this page during implementation. No claim is made that Plex ignores NFO. Agent/version interoperability still needs integration testing.
- [tinyMediaManager movie NFO formats](https://www.tinymediamanager.org/docs/movies/nfo-formats): XML interoperability conventions, with original-document preservation in export.
- [BurnAware features](https://www.burnaware.com/features.html): external GUI data-disc workflow; no direct or headless burn implementation.

All screenshots and examples use invented titles and generated fixture payloads. No copyrighted poster assets, movie files, personal inventories or external service credentials are included.
