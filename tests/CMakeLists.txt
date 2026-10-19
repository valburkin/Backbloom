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
