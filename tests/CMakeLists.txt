find_package(Qt6 REQUIRED COMPONENTS Test)
include(OverglowTest)

# objects of the product libraries are built with the shared precompiled header
if (TARGET precompiled_headers)
	set(PCH_OBJECTS precompiled_headers)
endif()

overglow_add_test(test_ledstring
	SOURCES
		unit/LedStringTest.cpp
		${CMAKE_SOURCE_DIR}/sources/base/LedString.cpp
	LIBS
		Qt6::Core
	LABELS unit)

overglow_add_test(test_settings_migration
	SOURCES
		unit/SettingsMigrationTest.cpp
		${CMAKE_SOURCE_DIR}/sources/base/SettingsMigration.cpp
	LIBS
		Qt6::Core
	LABELS unit)

# ColorSpace.cpp has a known C4756 that the product build reports already
set_source_files_properties(${CMAKE_SOURCE_DIR}/sources/infinite-color-engine/ColorSpace.cpp PROPERTIES COMPILE_OPTIONS /wd4756)

overglow_add_test(test_hdr_lut
	SOURCES
		unit/HdrLutTest.cpp
		${CMAKE_SOURCE_DIR}/sources/utils/DefaultLut.cpp
		${CMAKE_SOURCE_DIR}/sources/utils/LutLoader.cpp
		${CMAKE_SOURCE_DIR}/include/utils/Logger.h
		${CMAKE_SOURCE_DIR}/sources/utils/Logger.cpp
		${CMAKE_SOURCE_DIR}/sources/utils/FileUtils.cpp
		${CMAKE_SOURCE_DIR}/sources/utils/InternalClock.cpp
		${CMAKE_SOURCE_DIR}/sources/infinite-color-engine/ColorSpace.cpp
	LIBS
		Qt6::Core
		linalg
		image
		utils-zstd
		${PCH_OBJECTS}
	LABELS unit)

overglow_add_test(test_effects
	SOURCES
		unit/EffectsTest.cpp
		${CMAKE_SOURCE_DIR}/sources/effects/AnimationBase.cpp
		${CMAKE_SOURCE_DIR}/sources/effects/EffectManufactory.cpp
		${CMAKE_SOURCE_DIR}/sources/effects/Animation_Swirl.cpp
		${CMAKE_SOURCE_DIR}/sources/effects/Animation_MoodBlobs.cpp
		${CMAKE_SOURCE_DIR}/sources/effects/Animation_Fade.cpp
		${CMAKE_SOURCE_DIR}/sources/effects/Animation_CandleLight.cpp
	LIBS
		Qt6::Core
		image
		hyperimage
		${PCH_OBJECTS}
	LABELS unit)

# Golden fixtures for the .NET backend: each test runs C++ reference code on deterministic inputs and compares the
# result with the JSON under backend/tests/fixtures (OVERGLOW_UPDATE_FIXTURES=1 rewrites it), see golden/GoldenFixture.h
set(GOLDEN_COMMON_SOURCES
	golden/GoldenFixture.h
	golden/GoldenFixture.cpp
	golden/TestImages.h
	golden/TestImages.cpp
	${CMAKE_SOURCE_DIR}/include/utils/Logger.h
	${CMAKE_SOURCE_DIR}/sources/utils/Logger.cpp
	${CMAKE_SOURCE_DIR}/sources/utils/FileUtils.cpp
	${CMAKE_SOURCE_DIR}/sources/utils/InternalClock.cpp)

# Qt headers disable C4244 (qcompilerdetection.h); these product sources include ColorRgb.h before any Qt header when they
# are built without the product's precompiled header and then report C4244 in ColorRgb::fromHsv
set_source_files_properties(
	${CMAKE_SOURCE_DIR}/sources/base/ImageColorAveraging.cpp
	${CMAKE_SOURCE_DIR}/sources/grabber/windows/DX/BgraDecoder.cpp
	${CMAKE_SOURCE_DIR}/sources/base/SoundCaptureResult.cpp
	PROPERTIES COMPILE_OPTIONS /wd4244)

function(overglow_add_golden_test name)
	cmake_parse_arguments(PARSE_ARGV 1 ARG "" "" "SOURCES;LIBS")
	overglow_add_test(${name}
		SOURCES ${ARG_SOURCES} ${GOLDEN_COMMON_SOURCES}
		LIBS Qt6::Core linalg image ${ARG_LIBS} ${PCH_OBJECTS}
		LABELS golden)
	target_compile_definitions(${name} PRIVATE OVERGLOW_GOLDEN_FIXTURES_DIR="${CMAKE_SOURCE_DIR}/backend/tests/fixtures")
endfunction()

overglow_add_golden_test(test_golden_mapping
	SOURCES
		golden/MappingGolden.cpp
		${CMAKE_SOURCE_DIR}/sources/base/LedString.cpp
		${CMAKE_SOURCE_DIR}/sources/base/ImageColorAveraging.cpp)

overglow_add_golden_test(test_golden_color
	SOURCES
		golden/ColorGolden.cpp
		${CMAKE_SOURCE_DIR}/include/infinite-color-engine/InfiniteProcessing.h
		${CMAKE_SOURCE_DIR}/sources/infinite-color-engine/InfiniteProcessing.cpp
		${CMAKE_SOURCE_DIR}/sources/infinite-color-engine/ColorSpace.cpp
		${CMAKE_SOURCE_DIR}/sources/led-drivers/ColorRgbw.cpp
		${CMAKE_SOURCE_DIR}/sources/led-drivers/InfiniteColorEngineRgbw.cpp
		${CMAKE_SOURCE_DIR}/sources/base/LedString.cpp)

overglow_add_golden_test(test_golden_blackborder
	SOURCES
		golden/BlackBorderGolden.cpp
		${CMAKE_SOURCE_DIR}/sources/blackborder/BlackBorderDetector.cpp)

overglow_add_golden_test(test_golden_hdr
	SOURCES
		golden/HdrGolden.cpp
		${CMAKE_SOURCE_DIR}/sources/grabber/windows/DX/BgraDecoder.cpp
		${CMAKE_SOURCE_DIR}/sources/utils/DefaultLut.cpp
		${CMAKE_SOURCE_DIR}/sources/utils/LutLoader.cpp
		${CMAKE_SOURCE_DIR}/sources/infinite-color-engine/ColorSpace.cpp
	LIBS
		utils-zstd)

overglow_add_golden_test(test_golden_smoothing
	SOURCES
		golden/SmoothingGolden.cpp
		${CMAKE_SOURCE_DIR}/sources/infinite-color-engine/InfiniteStepperInterpolator.cpp
		${CMAKE_SOURCE_DIR}/sources/infinite-color-engine/InfiniteRgbInterpolator.cpp
		${CMAKE_SOURCE_DIR}/sources/infinite-color-engine/InfiniteYuvInterpolator.cpp
		${CMAKE_SOURCE_DIR}/sources/infinite-color-engine/InfiniteHybridInterpolator.cpp
		${CMAKE_SOURCE_DIR}/sources/infinite-color-engine/InfiniteHybridRgbInterpolator.cpp
		${CMAKE_SOURCE_DIR}/sources/infinite-color-engine/InfiniteExponentialInterpolator.cpp
		${CMAKE_SOURCE_DIR}/sources/infinite-color-engine/ColorSpace.cpp)

overglow_add_golden_test(test_golden_effects
	SOURCES
		golden/EffectsGolden.cpp
		${CMAKE_SOURCE_DIR}/sources/base/LedString.cpp
		${CMAKE_SOURCE_DIR}/include/base/SoundCapture.h
		${CMAKE_SOURCE_DIR}/sources/base/SoundCapture.cpp
		${CMAKE_SOURCE_DIR}/sources/base/SoundCaptureResult.cpp
		${CMAKE_SOURCE_DIR}/sources/effects/AnimationBase.cpp
		${CMAKE_SOURCE_DIR}/sources/effects/AnimationBaseMusic.cpp
		${CMAKE_SOURCE_DIR}/sources/effects/EffectManufactory.cpp
		${CMAKE_SOURCE_DIR}/sources/effects/Animation_Swirl.cpp
		${CMAKE_SOURCE_DIR}/sources/effects/Animation_MoodBlobs.cpp
		${CMAKE_SOURCE_DIR}/sources/effects/Animation_Fade.cpp
		${CMAKE_SOURCE_DIR}/sources/effects/Animation_CandleLight.cpp
		${CMAKE_SOURCE_DIR}/sources/effects/Animation4Music_PulseMulti.cpp
		${CMAKE_SOURCE_DIR}/sources/effects/Animation4Music_QuatroMulti.cpp
		${CMAKE_SOURCE_DIR}/sources/effects/Animation4Music_StereoMulti.cpp
		${CMAKE_SOURCE_DIR}/sources/effects/Animation4Music_WavesPulse.cpp
	LIBS
		hyperimage)
