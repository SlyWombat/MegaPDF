# MegaPDF #146 — Android SDK + emulator in a container.
# Nothing is installed on the host: JDK, SDK, NDK, CMake and the emulator all
# live in this image, and the emulator talks to the host's /dev/kvm.
FROM ubuntu:24.04

ENV DEBIAN_FRONTEND=noninteractive
RUN apt-get update && apt-get install -y --no-install-recommends \
        openjdk-17-jdk-headless \
        curl wget unzip zip git python3 python3-pip python3-venv \
        ca-certificates file xz-utils procps \
        qpdf poppler-utils \
        # the emulator's own qemu needs these even with -no-window
        libpulse0 libnss3 libxcomposite1 libxcursor1 libxi6 libxtst6 \
        libasound2t64 libgl1 libglu1-mesa libx11-6 libxrandr2 libxdamage1 \
        libxkbcommon0 libdbus-1-3 libfontconfig1 libfreetype6 \
        mesa-utils libgbm1 libegl1 \
        socat netcat-openbsd imagemagick \
        # tools/capture-gate reads the screen for its person/language/privacy
        # checks, and reports them as `skip` without these — a gate that stands
        # a check down quietly is worse than no gate (#146 §3).
        tesseract-ocr tesseract-ocr-eng tesseract-ocr-fra \
    && rm -rf /var/lib/apt/lists/*

ENV JAVA_HOME=/usr/lib/jvm/java-17-openjdk-amd64
ENV ANDROID_HOME=/opt/android-sdk
ENV ANDROID_SDK_ROOT=/opt/android-sdk
ENV PATH=$PATH:$JAVA_HOME/bin:$ANDROID_HOME/cmdline-tools/latest/bin:$ANDROID_HOME/platform-tools:$ANDROID_HOME/emulator

# Android command-line tools
RUN mkdir -p $ANDROID_HOME/cmdline-tools \
    && cd /tmp \
    && wget -q https://dl.google.com/android/repository/commandlinetools-linux-11076708_latest.zip -O cmdline.zip \
    && unzip -q cmdline.zip -d $ANDROID_HOME/cmdline-tools \
    && mv $ANDROID_HOME/cmdline-tools/cmdline-tools $ANDROID_HOME/cmdline-tools/latest \
    && rm cmdline.zip

RUN mkdir -p ~/.android && touch ~/.android/repositories.cfg \
    && yes | sdkmanager --licenses > /dev/null 2>&1 || true

# Build side: platform 36 (compileSdk/targetSdk), build-tools, NDK + CMake for the
# JNI shim over PDFium.
RUN sdkmanager --install \
        "platform-tools" \
        "platforms;android-36" \
        "platforms;android-34" \
        "build-tools;36.0.0" \
        "cmake;3.22.1" \
        "ndk;27.0.12077973" \
    > /dev/null

# Emulator side. API 36 is what targetSdk 36 actually ships against; API 34 is the
# fallback if a 36 image misbehaves headless.
#
# API 33 `default` is not a spare: it is the image the **Android Screenshots**
# workflow runs on (`api-level: 33`, and reactivecircus/android-emulator-runner
# defaults `target` to `default`), so it is the only one that produces the pixels
# the Play gate set is actually shot from. It used to be installed by hand into a
# running container, which meant the store captures were reproducible only until
# that container was removed (#146).
RUN sdkmanager --install \
        "emulator" \
        "system-images;android-36;google_apis;x86_64" \
        "system-images;android-34;google_apis;x86_64" \
        "system-images;android-33;default;x86_64" \
    > /dev/null

RUN yes | sdkmanager --licenses > /dev/null 2>&1 || true

# Libraries the bundled qemu needs that the first layer missed. Appended rather
# than folded in, so the 15 GB of SDK layers above stay cached.
RUN apt-get update && apt-get install -y --no-install-recommends \
        libxkbfile1 libxcb-cursor0 libxcb-icccm4 libxcb-image0 libxcb-keysyms1 \
        libxcb-randr0 libxcb-render-util0 libxcb-shape0 libxcb-xinerama0 \
        libxcb-xkb1 libnspr4 libdrm2 libxext6 libxfixes3 \
    && rm -rf /var/lib/apt/lists/*

ENV GRADLE_USER_HOME=/work/gradle-home
WORKDIR /work
CMD ["/bin/bash"]
