cmake_minimum_required(VERSION 3.25.0)
cmake_policy(SET CMP0141 NEW)

message( STATUS "CMake Version: ${CMAKE_VERSION}" )

PROJECT(Overglow)

if (NOT WIN32)
	message( FATAL_ERROR "Overglow builds on Windows only." )
endif()

set(CMAKE_POSITION_INDEPENDENT_CODE ON)

set(OUR_CMAKE_MODULES ${CMAKE_CURRENT_SOURCE_DIR}/cmake/cmake_modules)
set(CMAKE_MODULE_PATH ${OUR_CMAKE_MODULES} ${CMAKE_MODULE_PATH})

# Product support/documentation page, the single place for it: shown by both web UIs (via sysinfo)
# and the installers. Empty means no support link is shown.
set(OVERGLOW_SUPPORT_URL "" CACHE STRING "Product support/documentation URL (empty: no link)")

# print version
include (version)
file (STRINGS "version" OVERGLOW_VERSION)
SetVersionNumber(OVERGLOW ${OVERGLOW_VERSION})


# Instruct CMake to run moc automatically when needed.
set(CMAKE_AUTOMOC ON)
# auto prepare .qrc files
set(CMAKE_AUTORCC ON)

set_property(GLOBAL PROPERTY USE_FOLDERS ON)

# Configure CCache if available
option(USE_CCACHE_CACHING "Enable CCache caching" ON )
if (USE_CCACHE_CACHING)
	find_program(CCACHE_FOUND ccache HINTS "${CMAKE_CURRENT_SOURCE_DIR}/build")
	if ( CCACHE_FOUND )
		if (DEFINED ENV{CCACHE_DIR})
			set (CCACHE_DIR_CACHE_LOCATION $ENV{CCACHE_DIR})
			message ( STATUS "Found CCache (${CCACHE_FOUND}) and its env settings: ${CCACHE_DIR_CACHE_LOCATION}. Will try to use it." )
		else()
			set (CCACHE_DIR_CACHE_LOCATION "$ENV{AppData}/.ccache")
			message ( STATUS "Found CCache (${CCACHE_FOUND}) and set ccache dir: ${CCACHE_DIR_CACHE_LOCATION}. Will try to use it." )
		endif()

		file(COPY_FILE
			${CCACHE_FOUND} ${CMAKE_BINARY_DIR}/cl.exe
			ONLY_IF_DIFFERENT
		)
		set(CMAKE_MSVC_DEBUG_INFORMATION_FORMAT "$<$<CONFIG:Debug,RelWithDebInfo>:Embedded>")
		message ( STATUS "Writing CCache config to user profile: $ENV{USERPROFILE}/.ccache/ccache.conf")
		configure_file(${OUR_CMAKE_MODULES}/ccache.windows.conf.in $ENV{USERPROFILE}/.ccache/ccache.conf)
		configure_file(${OUR_CMAKE_MODULES}/Directory.Build.props.in ${CMAKE_BINARY_DIR}/Directory.Build.props)
	else()
		set( USE_CCACHE_CACHING           OFF )
		message ( STATUS "CCache is not found" )
	endif(CCACHE_FOUND)
endif(USE_CCACHE_CACHING)

# find QT libs
find_package(Qt6 REQUIRED COMPONENTS Core Network SerialPort)
message( STATUS "Found Qt Version: ${Qt6Core_VERSION}" )

# Find OpenSSL
find_package(OpenSSL)
if (NOT OPENSSL_FOUND)
	message( WARNING "OpenSSL could not be found. Overglow HTTPS instance and Philips Hue support will be disabled." )
endif()

# Build options
message( STATUS "\nBUILD FEATURES")

option(USE_SHARED_LIBS "Prefer shared libraries over static ones" ON)
message( STATUS "USE_SHARED_LIBS = ${USE_SHARED_LIBS}")

option(USE_EMBEDDED_WEB_RESOURCES "Use embedded web-server resources" OFF)
message( STATUS "USE_EMBEDDED_WEB_RESOURCES = ${USE_EMBEDDED_WEB_RESOURCES}")

option(USE_PRECOMPILED_HEADERS "Enable precompiled headers (PCH)" ON)
message( STATUS "USE_PRECOMPILED_HEADERS = ${USE_PRECOMPILED_HEADERS}")

message( STATUS "USE_CCACHE_CACHING = ${USE_CCACHE_CACHING}")

message( STATUS "\n")

# next part

find_package(GitVersion)
find_package(zstd)

# configure a header file to pass some of the CMake settings
# to the source code
configure_file("${PROJECT_SOURCE_DIR}/OverglowConfig.h.in" "${PROJECT_BINARY_DIR}/OverglowConfig.h")
include_directories("${PROJECT_BINARY_DIR}")

# Define the global output path of binaries
SET(LIBRARY_OUTPUT_PATH    ${PROJECT_BINARY_DIR}/lib)
SET(EXECUTABLE_OUTPUT_PATH ${PROJECT_BINARY_DIR}/bin)

file(MAKE_DIRECTORY ${LIBRARY_OUTPUT_PATH})
file(MAKE_DIRECTORY ${EXECUTABLE_OUTPUT_PATH})

# Add the project include directory as additional include path
include_directories(${CMAKE_SOURCE_DIR}/include)

# Enable C++20
set(CMAKE_CXX_STANDARD 20)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
set(CMAKE_CXX_EXTENSIONS OFF)

add_compile_options(/W4)
message(STATUS "C++20 support has been enabled.")

# find Threads libs
find_package(Threads REQUIRED)

# MSVC options
find_package(WindowsSDK REQUIRED)
message(STATUS "WINDOWS SDK: ${WINDOWSSDK_LATEST_DIR} ${WINDOWSSDK_LATEST_NAME}")
message(STATUS "MSVC VERSION: ${MSVC_VERSION}")
set_property(DIRECTORY ${CMAKE_CURRENT_SOURCE_DIR} PROPERTY VS_STARTUP_PROJECT overglow)
add_definitions(/MP)

if(NOT DEFINED CMAKE_LIBRARY_OUTPUT_DIRECTORY)
	set(CMAKE_LIBRARY_OUTPUT_DIRECTORY
		${${PROJECT_NAME}_BINARY_DIR}/Bin
		CACHE PATH
		"Single Directory for all Libraries")
endif()

if(NOT DEFINED CMAKE_RUNTIME_OUTPUT_DIRECTORY)
	set(CMAKE_RUNTIME_OUTPUT_DIRECTORY
		${${PROJECT_NAME}_BINARY_DIR}/Bin
		CACHE PATH
		"Single Directory for all Executables")
endif()

# remove generated files on make cleaan too
LIST( APPEND GENERATED_QRC
	${CMAKE_BINARY_DIR}/WebConfig.qrc
	${CMAKE_BINARY_DIR}/OverglowConfig.h
)
set_directory_properties(PROPERTIES ADDITIONAL_MAKE_CLEAN_FILES "${GENERATED_QRC}" )

# uninstall target
configure_file( "${OUR_CMAKE_MODULES}/cmake_uninstall.cmake.in" "${CMAKE_CURRENT_BINARY_DIR}/cmake_uninstall.cmake" IMMEDIATE @ONLY)
add_custom_target(uninstall COMMAND ${CMAKE_COMMAND} -P ${CMAKE_CURRENT_BINARY_DIR}/cmake_uninstall.cmake)

# Add the source/lib directories
add_subdirectory(external)
add_subdirectory(sources)

# Add resources directory
add_subdirectory(resources)

# Unit tests (Qt Test, run with ctest)
option(BUILD_TESTING "Build the unit tests" ON)
if (BUILD_TESTING)
	enable_testing()
	add_subdirectory(tests)
endif()

# enable make package - no code after this line !
include (${CMAKE_CURRENT_SOURCE_DIR}/cmake/packages.cmake)

# external targets
if (TARGET core_mqtt AND TARGET stb AND TARGET lunasvg AND TARGET libzstd_static AND TARGET libzstd_shared AND TARGET clean-all AND TARGET sqlite3 AND TARGET precompiled_headers )
	set_target_properties(core_mqtt stb lunasvg resources uninstall libzstd_static libzstd_shared clean-all sqlite3 precompiled_headers PROPERTIES FOLDER ExternalLibsTargets)
else()
	set_target_properties(resources uninstall PROPERTIES FOLDER ExternalLibsTargets)
endif()
