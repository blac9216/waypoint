// Copyright 2026 Justin Black
//
// Licensed under the Apache License, Version 2.0 (the "License").
// You may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//
//     http://www.apache.org/licenses/LICENSE-2.0
//
// Unless required by applicable law or agreed to in writing, software
// distributed under the License is distributed on an "AS IS" BASIS,
// WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
// See the License for the specific language governing permissions and
// limitations under the License.

global using Xunit;

// xUnit's default class-level parallelism is deliberately left ON. Concurrent
// WebApplicationFactory<Program> hosts used to fail intermittently here, but the cause
// was this app's own Serilog bootstrap logger being frozen twice ("The logger is already
// frozen"), not a framework limitation — fixed at the source in Program.cs
// (UseSerilog(preserveStaticLogger: true)). Do not reintroduce
// [assembly: CollectionBehavior(DisableTestParallelization = true)]: it would hide the
// next regression of that kind instead of surfacing it.
