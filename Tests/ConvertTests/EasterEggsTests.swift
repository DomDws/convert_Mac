import Foundation
import Testing

@testable import Convert

struct EasterEggsTests {
    private let calendar = Calendar(identifier: .gregorian)

    private func date(_ year: Int, _ month: Int, _ day: Int) throws -> Date {
        try #require(calendar.date(from: DateComponents(year: year, month: month, day: day)))
    }

    @Test func birthdayMessageMatchesExpectedYears() throws {
        let cases = [
            (2027, "23rd"),
            (2028, "24th"),
            (2029, "25th"),
            (2030, "26th"),
        ]
        for (year, age) in cases {
            let msg = EasterEggs.birthdayMessage(on: try date(year, 6, 8), calendar: calendar)
            #expect(msg == "Happy \(age) Birthday, Lana!\n~ Dom")
        }
    }

    @Test func nonBirthdayDatesReturnNil() throws {
        #expect(EasterEggs.birthdayMessage(on: try date(2027, 6, 7), calendar: calendar) == nil)
        #expect(EasterEggs.birthdayMessage(on: try date(2027, 6, 9), calendar: calendar) == nil)
        #expect(EasterEggs.birthdayMessage(on: try date(2027, 1, 8), calendar: calendar) == nil)
    }

    @Test func ordinalFormatting() {
        let cases: [(Int, String)] = [
            (1, "1st"),
            (2, "2nd"),
            (3, "3rd"),
            (4, "4th"),
            (11, "11th"),
            (12, "12th"),
            (13, "13th"),
            (21, "21st"),
            (22, "22nd"),
            (23, "23rd"),
            (24, "24th"),
            (26, "26th"),
            (111, "111th"),
        ]
        for (number, expected) in cases {
            #expect(EasterEggs.ordinal(number) == expected)
        }
    }

    @Test func testDateEnvOverride() {
        let override = EasterEggs.currentDate(env: ["CONVERT_TEST_DATE": "2027-06-08"], calendar: calendar)
        #expect(EasterEggs.birthdayMessage(on: override, calendar: calendar) == "Happy 23rd Birthday, Lana!\n~ Dom")

        for bad in ["bad", "2027-02-30", "2027-06"] {
            let fallback = EasterEggs.currentDate(env: ["CONVERT_TEST_DATE": bad], calendar: calendar)
            #expect(abs(fallback.timeIntervalSinceNow) < 10)
        }
    }

    @Test func shinyDoneMessages() {
        for roll in 1..<EasterEggs.shinyOdds {
            #expect(EasterEggs.doneMessage(count: 1, roll: roll, suffixIndex: 0) == "Converted 1 file")
            #expect(EasterEggs.doneMessage(count: 3, roll: roll, suffixIndex: 0) == "Converted 3 files")
        }
        for (idx, suffix) in EasterEggs.shinySuffixes.enumerated() {
            #expect(EasterEggs.doneMessage(count: 1, roll: 0, suffixIndex: idx) == "Converted 1 file" + suffix)
            #expect(EasterEggs.doneMessage(count: 2, roll: 0, suffixIndex: idx) == "Converted 2 files" + suffix)
        }
    }

    @Test func aboutCreditLineMentionsNames() {
        #expect(EasterEggs.aboutCreditLine.contains("Lana"))
        #expect(EasterEggs.aboutCreditLine.contains("Dom"))
    }
}
