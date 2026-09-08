using Microsoft.AspNetCore.Mvc;
using Baihua.Contracts.LocalModels;
using Baihua.Contracts.OpenClaw;
using Baihua.Family.Services;

namespace Baihua.Family.Controllers;

public partial class LocalModelDeploymentController
{
    private static string GetPlatformDefaultDirectory()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(home, ".baihua", "models");
    }
}
