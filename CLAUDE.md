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

`wwwroot/staticwebapp.config.json` rewrites unknown paths to `/index.html` so that any path loads the app instead of Azure's 404 page; the app's router then takes over (see Routing below). It lives in `wwwroot/` so that publish copies it into the uploaded folder; Azure ignores it anywhere else.

## Architecture

### Namespaces

The root namespace is `personal_website` (the hyphen in the project name becomes an underscore). `Shared/WebGLGraphics.cs` is the exception: it declares no namespace, so `WebGLGraphics` lives in the global namespace. It is the only file in `Shared/`, so there is no `personal_website.Shared` namespace to import.

### Routing

The site has two pages. `Pages/Index.razor` at `/` shows the cube with the GitHub and LinkedIn icon links beneath it, and under those a text link to the second page. `Pages/Lighting.razor` at `/lighting` explains how the cube is lit (see below), and sets its own tab title with `<PageTitle>`; the home page keeps the title in `index.html`. There is no layout component and no shared navigation: each page carries its own link to the other. `Pages/NotFound.razor` is the router's `NotFoundPage` and renders nothing: it redirects every unknown address (including the old `/projects` URLs) to `/`.

### Home page cube (`Pages/Index.razor`, `Shared/WebGLGraphics.cs`, `wwwroot/index.html`)

The rotating cube spans three files, and the animation loop is driven from JavaScript rather than from .NET:

1. The inline `<script>` at the bottom of `wwwroot/index.html` starts a `requestAnimationFrame` loop at page load that runs for the lifetime of the app, on every page. Each frame it calls `OnNextFrame` on `window.dotNetHelper` if one is registered; `window.onresize` calls `OnWindowResize` the same way.
2. `Index.razor` registers itself as that helper (`setDotNetHelper`) in `OnInitializedAsync` and clears it (`disposeDotNetHelper`) in `Dispose`, so frames only reach .NET while the home page is mounted. The `[JSInvokable]` method names must match the strings in `index.html`.
3. `OnNextFrame` rotates the model matrix by the elapsed time and calls `WebGLGraphics.Render()`. `WebGLGraphics` wraps the `Blazor.Extensions.Canvas` WebGL context (WebGL 1, so the shaders are GLSL ES 1.00): it owns the GLSL shader sources (raw string constants), buffer setup, and hand-written 4x4 matrices stored as column-major `float[16]` arrays (`RotateX/Y/Z` mutate the model matrix in place).

The canvas is sized to 75% of the smaller window dimension via the `getWindowDimensions` JS function; a resize must also call `UpdateViewport`. The cube's geometry (vertices, normals and indexes) is defined inline in `Index.razor`. Each face has four vertices of its own so that it can have its own normal, and its two triangles are wound counter-clockwise as seen from outside the cube.

The cube is polished gold. A metal shows only its surroundings, so the fragment shader reflects the view direction off the face and looks it up in a procedural "studio" of soft light panels (the `studio` function), tinted with the site's gold. There are no textures and no lighting uniforms: every lighting constant is in the shader source, which keeps `Render()` to the same few JS interop calls per frame. Lighting is done in view space, and normals are transformed with the model and view matrices directly, which is only correct while those matrices do nothing but rotate and translate.

`Pages/Lighting.razor` describes that studio to visitors with two SVG drawings and a table. Its bearings, heights and brightnesses are copied by hand from the `studio` function, so a change to a light in the shader needs the same change on that page. Nothing in its drawings is referenced by `url(#id)` (no markers, no gradients): with `<base href="/">` some browsers resolve that against the home page's address and find nothing, so arrowheads are polygons and fades are drawn as thin slices.

Hidden faces are removed twice over. Back-face culling drops every triangle facing away from the camera, which relies on that counter-clockwise winding: geometry wound the other way would vanish. The depth test then sorts out whatever is left, and needs the projection's near plane (`zMin`) to be above 0: at 0 every fragment gets the same depth and faces are drawn over each other in index order.

### Styling

There is no CSS framework. Global styles are in `wwwroot/css/app.css`, which also carries the small reset the page relies on (`box-sizing: border-box`, zero body margin). `app.css` also declares `color-scheme: dark` for the whole site, so scrollbars are dark and text with no colour of its own is white; the error bar opts back to light. Component-specific styles are in scoped `*.razor.css` files next to the component (`Pages/Index.razor.css` and `Pages/Lighting.razor.css`), which the build bundles into `personal-website.styles.css`; `index.html` links that bundle, so removing the last scoped file would leave a dead link. A scoped file cannot reach `:root`, so the lighting page keeps its colours as custom properties on its `.lighting` wrapper. The canvas element is rendered by the child `BECanvas` component, so scoped rules need `::deep` to reach it. The pre-boot loading spinner is plain HTML inside `#app` in `index.html`, styled by `.loading` / `.loader` in `app.css`; Blazor replaces it once the app starts.
