using SvelteSharp.Models;

namespace HomeLab.Server;

[SvelteModel(Name = "SveltePage")]
public sealed record SveltePageModel(
    bool IsAuthenticated,
    string? UserName,
    string Path);
