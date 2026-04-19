using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using EMS.Gateway.CommandHandler;
using EMS.Gateway.Contracts;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddSingleton<ICommandHandler, CommandHandlerService>();
builder.Services.AddHostedService<CommandHandlerService>(sp => (CommandHandlerService)sp.GetRequiredService<ICommandHandler>());

var host = builder.Build();
host.Run();
