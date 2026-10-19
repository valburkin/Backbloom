#=============================================================================
# SQLite
#=============================================================================

if (NOT USE_SHARED_LIBS)
	add_library(sqlite3 "${CMAKE_CURRENT_SOURCE_DIR}/sqlite/sqlite3.c")
else()
	add_library(sqlite3 SHARED "${CMAKE_CURRENT_SOURCE_DIR}/sqlite/sqlite3.c")
	if(WIN32)
	   target_compile_definitions(sqlite3
		  INTERFACE
			 "SQLITE_API=__declspec(dllimport)"
		  PRIVATE
			 "SQLITE_API=__declspec(dllexport)"
	   )
	else()
	   target_compile_definitions(sqlite3
		  INTERFACE
			 "SQLITE_API=__attribute__((visibility(\"default\")))"
	   )
	endif()
endif()

target_include_directories(sqlite3 PUBLIC "${CMAKE_CURRENT_SOURCE_DIR}/sqlite")
target_link_libraries(sqlite3 PRIVATE ${CMAKE_THREAD_LIBS_INIT} ${CMAKE_DL_LIBS})

install(
    TARGETS   sqlite3
    LIBRARY   DESTINATION "${CMAKE_INSTALL_LIBDIR}"
    ARCHIVE   DESTINATION "${CMAKE_INSTALL_LIBDIR}"
)

set_target_properties(sqlite3 PROPERTIES
    OUTPUT_NAME   sqlite3
)

target_compile_definitions(sqlite3 PUBLIC
	SQLITE_THREADSAFE=2
	SQLITE_DEFAULT_MEMSTATUS=0
	SQLITE_DEFAULT_SYNCHRONOUS=3
	SQLITE_OMIT_AUTHORIZATION
	SQLITE_OMIT_DEPRECATED
)

if(MSVC)
    target_compile_options(sqlite3 PRIVATE /W0)
else()
    target_compile_options(sqlite3 PRIVATE -w)
endif()

#=============================================================================
# LINALG
#=============================================================================

add_library(linalg INTERFACE)
target_compile_definitions(linalg INTERFACE LINALG_FORWARD_COMPATIBLE )
target_include_directories(linalg INTERFACE "${CMAKE_CURRENT_SOURCE_DIR}/linalg")

#=============================================================================
# LUNASVG
#=============================================================================

set(no_dev_warnings_backup "$CACHE{CMAKE_WARN_DEPRECATED}")
set(CMAKE_WARN_DEPRECATED OFF CACHE INTERNAL "" FORCE)

if (USE_SYSTEM_LUNASVG_LIBS)
	find_package(lunasvg GLOBAL)
	if (NOT lunasvg_FOUND)
		message(WARNING "Could NOT find lunasvg system libraries. Fallback to lunasvg submodule.")
	endif()
endif()

if (NOT USE_SYSTEM_LUNASVG_LIBS OR NOT lunasvg_FOUND)
	set(LUNASVG_BUILD_EXAMPLES OFF CACHE BOOL "Build examples" FORCE)
	set(PLUTOVG_BUILD_EXAMPLES OFF CACHE BOOL "Build examples" FORCE)


	add_subdirectory(${CMAKE_CURRENT_SOURCE_DIR}/lunasvg)
	set(lunasvg_targets lunasvg plutovg)

	foreach(target IN LISTS lunasvg_targets)
		set(opts_before)
		set(opts_after)
		get_target_property(opts_before ${target} COMPILE_OPTIONS)

		if(MSVC)
			set_target_properties(${target} PROPERTIES MSVC_WARNING_LEVEL 0)
			set(opts_after ${opts_before})
			if(opts_after)
				list(FILTER opts_after EXCLUDE REGEX "/W[0-4]|/w")
			endif()
			list(APPEND opts_after "/W0")
		else()
			foreach(f ${opts_before})
				if(NOT f MATCHES "^-w$|^-Wall$|^-Wextra$")
					list(APPEND opts_after ${f})
				endif()
			endforeach()
			list(APPEND opts_after "-w")
		endif()

		set_target_properties(${target} PROPERTIES COMPILE_OPTIONS "${opts_after}")
		get_target_property(opts_now ${target} COMPILE_OPTIONS)
		message("Lunasvg target '${target}' compile options before: ${opts_before}  after: ${opts_now}")
	endforeach()
endif()

set(CMAKE_WARN_DEPRECATED ${no_dev_warnings_backup} CACHE INTERNAL "" FORCE)

#=============================================================================
# MQTT
#=============================================================================

if ( ENABLE_MQTT )
		add_subdirectory(${CMAKE_CURRENT_SOURCE_DIR}/core_mqtt)
		set_target_properties(core_mqtt PROPERTIES INTERFACE_SOURCES "")
		target_include_directories(core_mqtt PUBLIC ${CMAKE_SOURCE_DIR}/include/mqtt)
endif()

#=============================================================================
# ZSTD
#=============================================================================

if ( ENABLE_ZSTD )
	if (NOT zstd_FOUND)
		message( STATUS "System library zstd could not be found. Using embedded zstd library.")
		if(WIN32)
			set(CMAKE_RC_FLAGS "${CMAKE_RC_FLAGS} -I${CMAKE_SOURCE_DIR}/external/zstd/lib")
		endif()
		set(ZSTD_BUILD_TESTS OFF)
		set(ZSTD_LEGACY_SUPPORT OFF)
		set(ZSTD_BUILD_PROGRAMS OFF)
		add_subdirectory(${CMAKE_CURRENT_SOURCE_DIR}/zstd/build/cmake)
		set_target_properties(libzstd PROPERTIES INTERFACE_INCLUDE_DIRECTORIES $<BUILD_INTERFACE:${CMAKE_SOURCE_DIR}/external/zstd/lib>)
		add_library(zstd::zstd ALIAS libzstd)
	endif()
ENDIF()

#=============================================================================
# STB
#=============================================================================

add_library(stb INTERFACE)

if (USE_SYSTEM_STB_LIBS)
	find_path(STB_INCLUDE_DIR stb_image.h PATH_SUFFIXES stb)
	if (NOT STB_INCLUDE_DIR)
		message(WARNING "Could NOT find system STB libraries. Falling back to embedded STB.")
		target_include_directories(stb INTERFACE ${CMAKE_CURRENT_SOURCE_DIR}/stb)
	else()
		message(STATUS "Found system STB libraries: ${STB_INCLUDE_DIR}")
		target_include_directories(stb INTERFACE ${STB_INCLUDE_DIR})
	endif()
else()
	target_include_directories(stb INTERFACE ${CMAKE_CURRENT_SOURCE_DIR}/stb)
endif()

target_compile_definitions(stb INTERFACE
	STB_IMAGE_WRITE_IMPLEMENTATION
	STB_IMAGE_IMPLEMENTATION
)
