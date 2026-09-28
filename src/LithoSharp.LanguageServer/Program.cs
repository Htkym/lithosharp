using LithoSharp.LanguageServer;

var server = new LspServer();
return await server.RunAsync().ConfigureAwait(false);
