var builder = DistributedApplication.CreateBuilder(args);

builder.AddProject<Projects.HomeLab>("homelab");

builder.Build().Run();
