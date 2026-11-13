// org-directory.exe: every command, with output. Output is Unicode to a console and UTF-8 when redirected (see
// ConsoleText).
var output = OrgDirectory.Interim.ConsoleText.Out;
return OrgDirectory.Cli.Run(args, new OrgDirectory.CliHost
{
    Out = output,
    Err = OrgDirectory.Interim.ConsoleText.Error,
    LogEcho = output,
});
