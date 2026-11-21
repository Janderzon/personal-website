# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this is

James Anderson's personal website (https://www.jamesaanderson.uk): a standalone Blazor WebAssembly app targeting .NET 10, hosted on Azure Static Web Apps. There is no server project, no API, no solution file and no test project — the single `personal-website.csproj` sits at the repo root.

## Commands

```bash
dotnet build          # compile
dotnet run            # dev server at http://localhost:5269 and https://localhost:7192
dotnet watch run      # dev server with hot reload (opens a browser)
dotnet publish personal-website.csproj -c Release -o bin/publish   # production build, as CI runs it; the site is bin/publish/wwwroot
```

There are no tests and no linter. The published build is trimmed, so a change that relies on reflection should be checked against the publish output, not just `dotnet run`.

## Deployment

`.github/workflows/azure-static-web-apps-black-moss-00039ee03.yml` deploys to Azure Static Web Apps:

- A pull request against `main` gets its own staging site (torn down when the PR closes).
- A push to `main` deploys production.

So changes go through a feature branch and a PR, never straight to `main`.

The workflow builds the site itself with the publish command above and uploads `bin/publish/wwwroot` with `skip_app_build: true`. Azure's own builder (Oryx) is deliberately not used: it lags behind new .NET SDK releases.

`wwwroot/staticwebapp.config.json` rewrites unknown paths to `/index.html` so that deep links to client-side routes (e.g. `/projects/building-my-website`) work in production. It lives in `wwwroot/` so that publish copies it into the uploaded folder; Azure ignores it anywhere else.

## Architecture

### Namespaces

The root namespace is `personal_website` (the hyphen in the project name becomes an underscore). `Shared/WebGLGraphics.cs` is the exception: it declares no namespace, so `WebGLGraphics` lives in the global namespace.

### Home page cube (`Pages/Index.razor`, `Shared/WebGLGraphics.cs`, `wwwroot/index.html`)

The rotating cube spans three files, and the animation loop is driven from JavaScript rather than from .NET:

1. The inline `<script>` at the bottom of `wwwroot/index.html` starts a `requestAnimationFrame` loop at page load that runs for the lifetime of the app, on every page. Each frame it calls `OnNextFrame` on `window.dotNetHelper` if one is registered; `window.onresize` calls `OnWindowResize` the same way.
2. `Index.razor` registers itself as that helper (`setDotNetHelper`) in `OnInitializedAsync` and clears it (`disposeDotNetHelper`) in `Dispose`, so frames only reach .NET while the home page is mounted. The `[JSInvokable]` method names must match the strings in `index.html`.
3. `OnNextFrame` rotates the model matrix by the elapsed time and calls `WebGLGraphics.Render()`. `WebGLGraphics` wraps the `Blazor.Extensions.Canvas` WebGL context: it owns the GLSL shader sources (string constants), buffer setup, and hand-written 4x4 matrices stored as column-major `float[16]` arrays (`RotateX/Y/Z` mutate the model matrix in place).

The canvas is sized to 75% of the smaller window dimension via the `getWindowDimensions` JS function; a resize must also call `UpdateViewport`. The cube's geometry (vertices and indexes) is defined inline in `Index.razor`.

### Projects page (`Pages/Projects.razor`, `Shared/ProjectsList.razor`, `wwwroot/Projects/*.json`)

The project tables are data-driven. `ProjectsList` fetches the JSON file named by its `ProjectsFileLoc` parameter over `HttpClient` and renders one row per entry. Adding or editing a project means editing `wwwroot/Projects/CodingProjects.json` or `EngineeringProjects.json`, not markup. Entry fields: `Year`, `Title`, `TechUsed`, plus optional `Link` (makes the title a link, used for internal write-up pages) and `GitHub` (shows the GitHub icon).

A project write-up is a Razor page under `Pages/` routed at `/projects/<slug>` (see `BuildingMyWebsite.razor`) and referenced from the JSON entry's `Link`.

### Styling

Global styles are in `wwwroot/css/app.css` (on top of the bundled Bootstrap and Open Iconic). Component-specific styles are in scoped `*.razor.css` files next to the component, which the build bundles into `personal-website.styles.css`. The pre-boot loading spinner is plain HTML inside `#app` in `index.html`, styled by `.loading` / `.loader` in `app.css`; Blazor replaces it once the app starts.
