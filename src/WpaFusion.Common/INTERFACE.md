# WpaFusion.Common interface

Status: implemented.

## Responsibility

Shared types, the clock map (TSC to QPC to wall clock), and the `session.json` reader. No dependencies.

## Known constraints

- The clock map is built only from `session.json` sync points (tightest of 16 rdtsc/QPC/precise-filetime triples at start and end).
- Per-CPU TSC offsets are applied before TSC to QPC conversion.
- `SepPlatform` parses `sep -version`, resolves older runs by matching the `.tb7` CPUID signature against `sep -pl`, and locates the platform's EDP metric file under `SEP_ROOT/config/edp`.
- Must handle 100M+ samples per session: conversion is allocation-free per sample.

## Public API

`SessionManifest`, `Timeline`, `ClockMap`, `SepPlatform`, `SyncPoint`, `TscOffsets`, and run-folder helpers. See source XML documentation for member details.
