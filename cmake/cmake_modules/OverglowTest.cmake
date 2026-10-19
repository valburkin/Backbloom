# Helper for Qt Test based unit tests run by ctest.
#
#   overglow_add_test(<name> SOURCES <files...> [LIBS <targets...>] [LABELS <labels...>])
#
# Builds the test executable into <build>/tests/<config>, links Qt Test and registers it with ctest.
# The Qt bin directory (and the directory of linked shared product libraries) is prepended to PATH for the test run
# (these DLLs are not deployed next to tests)
# and Qt runs with the offscreen platform plugin, so tests work headless. QT_FORCE_STDERR_LOGGING keeps the
# test log on stdout even when no console is attached (Qt on Windows sends it to the debugger otherwise).

set(OVERGLOW_TEST_FIXTURES_DIR "${CMAKE_SOURCE_DIR}/tests/fixtures")

function(overglow_add_test name)
	cmake_parse_arguments(PARSE_ARGV 1 ARG "" "" "SOURCES;LIBS;LABELS")
	if (NOT ARG_SOURCES)
		message(FATAL_ERROR "overglow_add_test(${name}): SOURCES is required")
	endif()

	add_executable(${name} ${ARG_SOURCES})
	target_link_libraries(${name} PRIVATE Qt6::Test ${ARG_LIBS})
	target_compile_definitions(${name} PRIVATE OVERGLOW_TEST_FIXTURES_DIR="${OVERGLOW_TEST_FIXTURES_DIR}")
	set_target_properties(${name} PROPERTIES
		RUNTIME_OUTPUT_DIRECTORY "${CMAKE_BINARY_DIR}/tests"
		FOLDER Tests)

	# DLLs of linked shared product libraries (e.g. utils-zstd with USE_SHARED_LIBS) are not next to the test either
	set(path_modifications "PATH=path_list_prepend:${QT6_INSTALL_PREFIX}/${QT6_INSTALL_BINS}")
	foreach(lib IN LISTS ARG_LIBS)
		if (TARGET ${lib})
			get_target_property(lib_type ${lib} TYPE)
			if (lib_type STREQUAL "SHARED_LIBRARY")
				list(APPEND path_modifications "PATH=path_list_prepend:$<TARGET_FILE_DIR:${lib}>")
			endif()
		endif()
	endforeach()

	add_test(NAME ${name} COMMAND ${name})
	set_tests_properties(${name} PROPERTIES
		LABELS "${ARG_LABELS}"
		ENVIRONMENT "QT_QPA_PLATFORM=offscreen;QT_FORCE_STDERR_LOGGING=1"
		ENVIRONMENT_MODIFICATION "${path_modifications}")
endfunction()
