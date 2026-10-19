# Define the current source locations
SET(CURRENT_HEADER_DIR ${CMAKE_SOURCE_DIR}/include/systray)
SET(CURRENT_SOURCE_DIR ${CMAKE_SOURCE_DIR}/sources/systray)

add_library(systray-widget
	${CURRENT_HEADER_DIR}/Systray.h
	${CURRENT_SOURCE_DIR}/SystrayWindows.cpp
)
