# Changelog

## [0.6.3](https://github.com/dcc-mcp/dcc-mcp-unity/compare/v0.6.2...v0.6.3) (2026-07-24)


### Bug Fixes

* exempt editor test runs from the batch-mode bridge guard ([5ff9381](https://github.com/dcc-mcp/dcc-mcp-unity/commit/5ff9381f9097c14d81adb0da72178f0be6d20b9f))
* prevent AssetImportWorker and batch-mode processes from connecting to bridge ([24f8daa](https://github.com/dcc-mcp/dcc-mcp-unity/commit/24f8daa319b44def5fcb836d940b64a768c228e0)), closes [#19](https://github.com/dcc-mcp/dcc-mcp-unity/issues/19)
* support UTF 1.1.x ExecutionSettings(params Filter[]) constructor ([#26](https://github.com/dcc-mcp/dcc-mcp-unity/issues/26)) ([b439cfe](https://github.com/dcc-mcp/dcc-mcp-unity/commit/b439cfe6f48ecbf7e50551dbe2deaff942dedd54)), closes [#20](https://github.com/dcc-mcp/dcc-mcp-unity/issues/20)

## [0.6.2](https://github.com/dcc-mcp/dcc-mcp-unity/compare/v0.6.1...v0.6.2) (2026-07-22)


### Bug Fixes

* report Unity bridge connection readiness ([2af8d3a](https://github.com/dcc-mcp/dcc-mcp-unity/commit/2af8d3a5c687ae0b900331729051a783077c0404))

## [0.6.1](https://github.com/dcc-mcp/dcc-mcp-unity/compare/v0.6.0...v0.6.1) (2026-07-22)


### Bug Fixes

* wait for Unity Play Mode transitions to settle ([#17](https://github.com/dcc-mcp/dcc-mcp-unity/issues/17)) ([fb0d7f0](https://github.com/dcc-mcp/dcc-mcp-unity/commit/fb0d7f096fbcb3cec912e88903bc899f0644e77e))

## [0.6.0](https://github.com/dcc-mcp/dcc-mcp-unity/compare/v0.5.1...v0.6.0) (2026-07-22)


### Features

* add typed Unity Test Runner jobs ([#15](https://github.com/dcc-mcp/dcc-mcp-unity/issues/15)) ([8f99bd4](https://github.com/dcc-mcp/dcc-mcp-unity/commit/8f99bd471f36a357aa1d17c54c24c1d7ece4df70))

## [0.5.1](https://github.com/dcc-mcp/dcc-mcp-unity/compare/v0.5.0...v0.5.1) (2026-07-21)


### Bug Fixes

* keep Unity bridge responsive while paused ([a435d61](https://github.com/dcc-mcp/dcc-mcp-unity/commit/a435d618207386e03f71d4767e1cd48d28daa81e))

## [0.5.0](https://github.com/dcc-mcp/dcc-mcp-unity/compare/v0.4.1...v0.5.0) (2026-07-21)


### Features

* add durable Unity game authoring tools ([#11](https://github.com/dcc-mcp/dcc-mcp-unity/issues/11)) ([936eac2](https://github.com/dcc-mcp/dcc-mcp-unity/commit/936eac2d417806c4fa20119c99bf33995dd2ec1f))

## [0.4.1](https://github.com/dcc-mcp/dcc-mcp-unity/compare/v0.4.0...v0.4.1) (2026-07-21)


### Bug Fixes

* allow manual PyPI publication ([8c7140f](https://github.com/dcc-mcp/dcc-mcp-unity/commit/8c7140f2fdc16f872f2a3b7746019fddb6009768))
* allow standalone asset rebuilds ([11777a0](https://github.com/dcc-mcp/dcc-mcp-unity/commit/11777a07ccc86556b132848a4e29854c899938c3))
* combine standalone matrix conditions ([5a48692](https://github.com/dcc-mcp/dcc-mcp-unity/commit/5a48692470312dcf6668e51d66229340bb51dee6))
* keep standalone workflow matrix compatible ([f5dba26](https://github.com/dcc-mcp/dcc-mcp-unity/commit/f5dba26e32e4631865b7e630dd439b464d01ff72))
* locate PyOxidizer install output ([41169bf](https://github.com/dcc-mcp/dcc-mcp-unity/commit/41169bf6979846c4de93b00929867c4ffb53984b))
* support macOS standalone rebuild ([cfaa80d](https://github.com/dcc-mcp/dcc-mcp-unity/commit/cfaa80d9460d1fd1492a173066a8df5b29ee7023))
* support Unity 2018.4.25f1 ([#9](https://github.com/dcc-mcp/dcc-mcp-unity/issues/9)) ([024dc87](https://github.com/dcc-mcp/dcc-mcp-unity/commit/024dc87e1d1e31a7c4652dfdc235c092d43804aa))
* use stable standalone asset names ([3786c4c](https://github.com/dcc-mcp/dcc-mcp-unity/commit/3786c4cefd2d7dc914a57007753299fdb04c6e8e))

## [0.4.0](https://github.com/dcc-mcp/dcc-mcp-unity/compare/v0.3.0...v0.4.0) (2026-07-21)


### Features

* add standalone Unity sidecar ([97fedb3](https://github.com/dcc-mcp/dcc-mcp-unity/commit/97fedb39c6c897ba7310ab9548f58cd68b140e2b))


### Bug Fixes

* keep standalone tests platform neutral ([c39ba5b](https://github.com/dcc-mcp/dcc-mcp-unity/commit/c39ba5b464c7adc60f1f8333d9e7e4b9993a8ff9))

## [0.3.0](https://github.com/dcc-mcp/dcc-mcp-unity/compare/v0.2.0...v0.3.0) (2026-07-21)


### Features

* support Unity 2018.4 through latest stable ([#2](https://github.com/dcc-mcp/dcc-mcp-unity/issues/2)) ([8f44d8e](https://github.com/dcc-mcp/dcc-mcp-unity/commit/8f44d8e7db1015c01286c3bbfb817bc3ad74bca9))

## [0.2.0](https://github.com/dcc-mcp/dcc-mcp-unity/compare/v0.1.0...v0.2.0) (2026-07-21)


### Features

* add Unity Editor MCP adapter ([5595603](https://github.com/dcc-mcp/dcc-mcp-unity/commit/55956032b33643df8deaf229f131b9d2378891aa))

## Changelog
