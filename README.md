```
C:\patch_sof2
├───New
│   │
│   └───Current
│           cg_draw.c
│
└───Old
    │   cg_draw.c
    │
    └───Diff
            cg_draw.c.diff
```

call it like:

```
C:\patch_sof2>FuzzyPatch.exe Old New
roslynPathOld: Old
roslynPathNew: New
_roslynPathOldDiff: Old\Diff
_roslynPathNewCurrent: New\Current
s=@@ -4399,7 +4399,7 @@
s=@@ -4296,7 +4296,7 @@
File in new revision updated: C:\patch_sof2\New\Current\cg_draw.c
  Lines removed = 2, lines added = 2, displacement = 112.
End of Fuzzy Patch program.
```
