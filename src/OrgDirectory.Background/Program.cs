// org-directory-background.exe: no window, ever. What the daily task runs (`--config SETTINGS sync --hook
// --scheduled`); anything else runs as the console program would, with nothing shown.
return OrgDirectory.Cli.Run(args, new OrgDirectory.CliHost { Out = TextWriter.Null, Err = TextWriter.Null });
