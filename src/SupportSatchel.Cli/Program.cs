using SupportSatchel.Core;

const int ExitOk = 0;

if (args.Length > 0 && args[0] is "--version" or "-v")
{
    Console.WriteLine($"Support Satchel CLI ({CoreInfo.Product}) 0.0.1");
    return ExitOk;
}

Console.WriteLine("Support Satchel CLI skeleton.");
Console.WriteLine("Commands (list profiles, run profile, export bundle) land with issue #7.");
Console.WriteLine("Pass --version to print version info.");
return ExitOk;
