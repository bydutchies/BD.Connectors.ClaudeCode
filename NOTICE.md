# Notice

BD.Connectors.ClaudeCode is a from-scratch .NET port, © 2026 byDutchies (MIT — see `LICENSE`), of
the behavior of two other MIT-licensed projects. Neither project's code is copied verbatim (C# is
not Python or a drop-in of the C# SDK below), but the public API shape, protocol handling and
several helper algorithms are deliberately modeled on them. Their licenses are reproduced below as
attribution.

## claude-agent-sdk-python

Version 0.2.158, the specification this port targets (`ClaudeAgentOptions`, message/content types,
the control protocol, session store semantics, `tool()`/`create_sdk_mcp_server()`, etc.).

```
MIT License

Copyright (c) 2025 Anthropic, PBC

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
```

## ClaudeCodeSharpSDK

Version 1.1.3, used as a source of reusable C# patterns and pure functions (CLI locator/version
helpers, structured-output deserialization, wire-value conversion patterns). Most of its production
code was superseded rather than reused, since its client model (thread-based, `--print` one-shot
subprocess calls) does not match the always-streaming control-protocol this port implements.

```
MIT License

Copyright (c) 2026 Managed Code

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
```

## Anthropic's terms of service

This SDK drives the `claude` CLI as a subprocess; it does not embed or redistribute it. Use of the
CLI itself is subject to Anthropic's own terms of service, independent of this project's MIT
license.
