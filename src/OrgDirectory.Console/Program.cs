// org-directory.exe: every command, with output. Output is Unicode to a console and UTF-8 when redirected (see
// ConsoleText).
var output = WinBackground.ConsoleText.Out;
return OrgDirectory.Cli.Run(args, new OrgDirectory.CliHost
{
    Out = output,
    Err = WinBackground.ConsoleText.Error,
    LogEcho = output,
});
