using FluentAssertions;
using IHostPro.Contexts.ExternalIntegrations.Application.AirbnbReservationParser;
using IHostPro.Contexts.ExternalIntegrations.Infrastructure.AirbnbReservationParser;

namespace IHostPro.Contexts.ExternalIntegrations.Tests.Unit.Infrastructure.AirbnbReservationParser;

// Every fixture below is entirely synthetic - "Hóspede Teste", "Studio
// Exemplo Fixture" and "TESTCODE12" are fabricated placeholder-style values
// that mimic the STRUCTURE of a real observed Airbnb "reservation reminder"
// email (a labeled reservation card: listing name + property type,
// Check-in/Checkout dates and times, guest count, confirmation code) -
// no real guest name, address, code, or amount from any real mailbox is
// reproduced anywhere in this repository.
public class AirbnbReservationReminderParserTests
{
    private const string ReservationReminderSubject = "Lembrete de reserva: Hóspede Teste chega em breve!";

    private const string ReservationReminderBody = """
        <html><body>
        <div>
        <p>Studio Exemplo Fixture</p>
        <p>Casa/apto inteiro</p>
        <table>
        <tr><td>Check-in</td><td>Checkout</td></tr>
        <tr><td>qui., 5 de nov.<br/>14:00</td><td>dom., 8 de nov.<br/>11:00</td></tr>
        </table>
        <p>Hóspedes</p>
        <p>2 adultos</p>
        <p>Código de confirmação</p>
        <p>TESTCODE12</p>
        </div>
        </body></html>
        """;

    private static readonly DateTimeOffset ReceivedAtUtc = new(2026, 11, 3, 9, 0, 0, TimeSpan.Zero);

    private readonly AirbnbReservationReminderParser _parser = new();

    [Fact]
    public void Parse_extracts_every_field_from_the_reservation_reminder_template()
    {
        var result = _parser.Parse(ReservationReminderSubject, ReservationReminderBody, ReceivedAtUtc);

        result.IsSuccess.Should().BeTrue();
        result.ExternalReservationId.Should().Be("TESTCODE12");
        result.GuestName.Should().Be("Hóspede Teste");
        result.ListingName.Should().Be("Studio Exemplo Fixture");
        result.GuestCount.Should().Be(2);
        result.CheckInAt.Should().Be(new DateTimeOffset(2026, 11, 5, 14, 0, 0, TimeSpan.Zero));
        result.CheckOutAt.Should().Be(new DateTimeOffset(2026, 11, 8, 11, 0, 0, TimeSpan.Zero));
    }

    [Fact]
    public void Parse_extracts_guest_name_from_the_body_contact_phrase_when_the_subject_has_no_usable_pattern()
    {
        // Real-mailbox validation (Fase 9 review) found a 0/5-reliable subject
        // for this template - this fixture models that reality: a subject
        // with no "chega em"/guest-name pattern at all.
        const string subject = "Lembrete de reserva - detalhes da sua estadia";
        const string body = """
            <p>Studio Exemplo Fixture</p>
            <p>Casa/apto inteiro</p>
            <p>Check-in</p><p>Checkout</p>
            <p>5 de nov.</p><p>14:00</p>
            <p>8 de nov.</p><p>11:00</p>
            <p>Caso ainda nao tenha feito isso, entre em contato com Hospede Teste para enviar instrucoes.</p>
            <p>Hóspedes</p>
            <p>2 adultos</p>
            <p>Código de reserva</p>
            <p>TESTCODE12</p>
            """;

        var result = _parser.Parse(subject, body, ReceivedAtUtc);

        result.IsSuccess.Should().BeTrue();
        result.GuestName.Should().Be("Hospede Teste");
    }

    [Fact]
    public void Parse_prefers_the_body_contact_phrase_over_the_subject_when_both_are_present()
    {
        const string subject = "Lembrete de reserva: Nome Do Subject chega em breve!";
        const string body = """
            <p>Studio Exemplo Fixture</p>
            <p>Casa/apto inteiro</p>
            <p>Check-in</p><p>Checkout</p>
            <p>5 de nov.</p><p>14:00</p>
            <p>8 de nov.</p><p>11:00</p>
            <p>entre em contato com Nome Corpo para enviar instrucoes.</p>
            <p>Hóspedes</p>
            <p>2 adultos</p>
            <p>Código de confirmação</p>
            <p>TESTCODE12</p>
            """;

        var result = _parser.Parse(subject, body, ReceivedAtUtc);

        result.IsSuccess.Should().BeTrue();
        result.GuestName.Should().Be("Nome Corpo");
    }

    [Fact]
    public void Parse_infers_next_year_when_the_dates_would_otherwise_fall_far_in_the_past()
    {
        // Reminder sent in December for a stay in early January - the
        // same-calendar-year date would be ~11 months in the past.
        const string body = """
            <p>Studio Exemplo Fixture</p>
            <p>Casa/apto inteiro</p>
            <p>Check-in</p><p>Checkout</p>
            <p>2 de jan.</p><p>14:00</p>
            <p>4 de jan.</p><p>11:00</p>
            <p>Hóspedes</p>
            <p>2 adultos</p>
            <p>Código de confirmação</p>
            <p>TESTCODE12</p>
            """;
        var receivedAtUtc = new DateTimeOffset(2026, 12, 28, 9, 0, 0, TimeSpan.Zero);

        var result = _parser.Parse(ReservationReminderSubject, body, receivedAtUtc);

        result.IsSuccess.Should().BeTrue();
        result.CheckInAt.Should().Be(new DateTimeOffset(2027, 1, 2, 14, 0, 0, TimeSpan.Zero));
        result.CheckOutAt.Should().Be(new DateTimeOffset(2027, 1, 4, 11, 0, 0, TimeSpan.Zero));
    }

    [Fact]
    public void Parse_returns_UnsupportedTemplate_when_the_body_has_no_reservation_card_landmarks()
    {
        const string body = "<p>Seu pagamento de R$ 100,00 foi processado.</p>";

        var result = _parser.Parse("Pagamento recebido", body, ReceivedAtUtc);

        result.IsSuccess.Should().BeFalse();
        result.FailureReason.Should().Be(AirbnbReservationReminderParseFailureReason.UnsupportedTemplate);
    }

    [Fact]
    public void Parse_returns_MissingRequiredField_when_guest_count_is_absent()
    {
        const string body = """
            <p>Studio Exemplo Fixture</p>
            <p>Casa/apto inteiro</p>
            <p>Check-in</p><p>Checkout</p>
            <p>5 de nov.</p><p>14:00</p>
            <p>8 de nov.</p><p>11:00</p>
            <p>Código de confirmação</p>
            <p>TESTCODE12</p>
            """;

        var result = _parser.Parse(ReservationReminderSubject, body, ReceivedAtUtc);

        result.IsSuccess.Should().BeFalse();
        result.FailureReason.Should().Be(AirbnbReservationReminderParseFailureReason.MissingRequiredField);
        result.MissingFieldName.Should().Be("GuestCount");
    }

    [Fact]
    public void Parse_returns_MissingRequiredField_when_the_confirmation_code_label_is_absent()
    {
        const string body = """
            <p>Studio Exemplo Fixture</p>
            <p>Casa/apto inteiro</p>
            <p>Check-in</p><p>Checkout</p>
            <p>5 de nov.</p><p>14:00</p>
            <p>8 de nov.</p><p>11:00</p>
            <p>Hóspedes</p>
            <p>2 adultos</p>
            """;

        var result = _parser.Parse(ReservationReminderSubject, body, ReceivedAtUtc);

        result.IsSuccess.Should().BeFalse();
        result.FailureReason.Should().Be(AirbnbReservationReminderParseFailureReason.MissingRequiredField);
        result.MissingFieldName.Should().Be("ExternalReservationId");
    }

    [Fact]
    public void Parse_returns_InvalidConfirmationCodeContext_when_the_value_after_the_label_has_the_wrong_shape()
    {
        const string body = """
            <p>Studio Exemplo Fixture</p>
            <p>Casa/apto inteiro</p>
            <p>Check-in</p><p>Checkout</p>
            <p>5 de nov.</p><p>14:00</p>
            <p>8 de nov.</p><p>11:00</p>
            <p>Hóspedes</p>
            <p>2 adultos</p>
            <p>Código de confirmação</p>
            <p>abc</p>
            """;

        var result = _parser.Parse(ReservationReminderSubject, body, ReceivedAtUtc);

        result.IsSuccess.Should().BeFalse();
        result.FailureReason.Should().Be(AirbnbReservationReminderParseFailureReason.InvalidConfirmationCodeContext);
    }

    [Fact]
    public void Parse_returns_MissingRequiredField_when_no_date_pattern_follows_the_checkin_checkout_labels()
    {
        const string body = """
            <p>Studio Exemplo Fixture</p>
            <p>Casa/apto inteiro</p>
            <p>Check-in</p><p>Checkout</p>
            <p>Hóspedes</p>
            <p>2 adultos</p>
            <p>Código de confirmação</p>
            <p>TESTCODE12</p>
            """;

        var result = _parser.Parse(ReservationReminderSubject, body, ReceivedAtUtc);

        result.IsSuccess.Should().BeFalse();
        result.FailureReason.Should().Be(AirbnbReservationReminderParseFailureReason.MissingRequiredField);
        result.MissingFieldName.Should().Be("CheckInAt/CheckOutAt");
    }

    [Fact]
    public void Parse_returns_MissingRequiredField_when_the_subject_does_not_match_the_reminder_pattern()
    {
        var result = _parser.Parse("Assunto qualquer sem o padrao esperado", ReservationReminderBody, ReceivedAtUtc);

        result.IsSuccess.Should().BeFalse();
        result.FailureReason.Should().Be(AirbnbReservationReminderParseFailureReason.MissingRequiredField);
        result.MissingFieldName.Should().Be("GuestName");
    }

    [Fact]
    public void Parse_handles_plain_text_input_without_any_html_tags()
    {
        const string body = """
            Studio Exemplo Fixture
            Casa/apto inteiro
            Check-in
            Checkout
            5 de nov.
            14:00
            8 de nov.
            11:00
            Hóspedes
            2 adultos
            Código de confirmação
            TESTCODE12
            """;

        var result = _parser.Parse(ReservationReminderSubject, body, ReceivedAtUtc);

        result.IsSuccess.Should().BeTrue();
        result.ExternalReservationId.Should().Be("TESTCODE12");
    }
}
