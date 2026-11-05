/*
 * Forever Scout – universell startare (Contents/MacOS/ForeverScout i det universella paketet).
 *
 * .NET:s självständiga publicering ger en mapp per arkitektur (osx-arm64, osx-x64) som inte går att slå ihop med
 * lipo (System.Private.CoreLib m.fl. är kompilerade per arkitektur). Paketet innehåller därför båda mapparna,
 * Contents/MacOS/arm64/ och Contents/MacOS/x64/, och den här lilla binären (byggd för arm64 + x86_64 med clang)
 * byter ut sig själv (execv, samma process-id) mot rätt arkitekturs ForeverScout. Inget annat: inga skript,
 * ingen nätverkstrafik, inga miljövariabler. Kompileras och signeras av build.sh.
 */
#include <errno.h>
#include <limits.h>
#include <mach-o/dyld.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <unistd.h>

#if defined(__arm64__) || defined(__aarch64__)
#define SCOUT_ARCH "arm64"
#else
#define SCOUT_ARCH "x64"
#endif

int main(int argc, char *argv[])
{
    (void)argc;
    char self[PATH_MAX];
    uint32_t size = sizeof(self);
    if (_NSGetExecutablePath(self, &size) != 0) return 70;
    char real[PATH_MAX];
    if (realpath(self, real) == NULL) return 71;
    char *slash = strrchr(real, '/');
    if (slash == NULL) return 72;
    *slash = '\0';
    char target[PATH_MAX];
    if (snprintf(target, sizeof(target), "%s/%s/ForeverScout", real, SCOUT_ARCH) >= (int)sizeof(target)) return 73;
    argv[0] = target;
    execv(target, argv);
    fprintf(stderr, "Forever Scout: kan inte starta %s: %s\n", target, strerror(errno));
    return 74;
}
